package sidecar

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"sync"
	"time"

	"github.com/msdickinson/vett/sidecar/pkg/rpc"
)

// SidecarVersion is stamped into hello responses.
const SidecarVersion = "0.1.0-dev"

// Server is the main sidecar event loop. One Server instance reads requests
// from a reader (usually os.Stdin) and writes responses to a writer (usually
// os.Stdout).
type Server struct {
	in  io.Reader
	out io.Writer

	outMu sync.Mutex

	mu       sync.Mutex
	sessions map[string]*Session
}

// NewServer constructs a Server.
func NewServer(in io.Reader, out io.Writer) *Server {
	return &Server{
		in:       in,
		out:      out,
		sessions: map[string]*Session{},
	}
}

// Session holds one bash child plus its per-session file editor state.
type Session struct {
	Name   string
	Bash   *BashSession
	Editor *FileEditor
	reqs   chan *pendingRequest
	done   chan struct{}  // closed when the session goroutine has exited
	closed sync.Once
}

type pendingRequest struct {
	req rpc.Request
}

// Serve runs the event loop until the input stream closes. On EOF it
// stops accepting new requests, drains in-flight per-session work, then
// kills bash children and returns.
func (s *Server) Serve(ctx context.Context) error {
	scanner := bufio.NewScanner(s.in)
	scanner.Buffer(make([]byte, 64*1024), 16*1024*1024)
	for scanner.Scan() {
		line := scanner.Bytes()
		if len(line) == 0 {
			continue
		}
		var req rpc.Request
		if err := json.Unmarshal(line, &req); err != nil {
			s.writeError("", rpc.ErrInvalidArgs, err.Error())
			continue
		}
		s.dispatch(ctx, &req)
	}
	s.drainAndShutdown()
	if err := scanner.Err(); err != nil {
		return err
	}
	return nil
}

func (s *Server) dispatch(ctx context.Context, req *rpc.Request) {
	// Management ops run on the main loop directly.
	switch req.Op {
	case rpc.OpHello:
		s.handleHello(req)
		return
	case rpc.OpSessionCreate:
		s.handleSessionCreate(req)
		return
	case rpc.OpSessionDestroy:
		s.handleSessionDestroy(req)
		return
	case rpc.OpSessionList:
		s.handleSessionList(req)
		return
	}
	// Session-scoped ops: route to the session's goroutine.
	sessionID, err := sessionIDOf(req)
	if err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	s.mu.Lock()
	sess := s.sessions[sessionID]
	s.mu.Unlock()
	if sess == nil {
		s.writeError(req.ID, rpc.ErrSessionNotFound, fmt.Sprintf("session %q does not exist", sessionID))
		return
	}
	select {
	case sess.reqs <- &pendingRequest{req: *req}:
	case <-sess.done:
		s.writeError(req.ID, rpc.ErrSessionNotFound, "session terminated")
	}
}

// drainAndShutdown closes each session's request channel, waits for the
// session goroutine to finish its in-flight work, then kills bash.
func (s *Server) drainAndShutdown() {
	s.mu.Lock()
	sessions := make([]*Session, 0, len(s.sessions))
	for _, sess := range s.sessions {
		sessions = append(sessions, sess)
	}
	s.sessions = map[string]*Session{}
	s.mu.Unlock()
	for _, sess := range sessions {
		sess.closeReqs()
	}
	for _, sess := range sessions {
		<-sess.done
		_ = sess.Bash.Close()
	}
}

// ---------------- management ops ----------------

func (s *Server) handleHello(req *rpc.Request) {
	var args rpc.HelloArgs
	_ = json.Unmarshal(req.Args, &args)
	res := rpc.HelloResult{
		SidecarVersion:  SidecarVersion,
		ProtocolVersion: rpc.ProtocolVersion,
		SupportedOps: []string{
			rpc.OpHello, rpc.OpSessionCreate, rpc.OpSessionDestroy, rpc.OpSessionList,
			rpc.OpBashExec, rpc.OpFileView, rpc.OpFileCreate, rpc.OpFileStrReplace,
			rpc.OpFileInsert, rpc.OpFileUndo, rpc.OpFileExists, rpc.OpListDir,
		},
	}
	if args.ProtocolVersion != 0 && args.ProtocolVersion != rpc.ProtocolVersion {
		s.writeError(req.ID, rpc.ErrInternal,
			fmt.Sprintf("protocol version mismatch: host=%d sidecar=%d", args.ProtocolVersion, rpc.ProtocolVersion))
		return
	}
	s.writeOK(req.ID, res)
}

func (s *Server) handleSessionCreate(req *rpc.Request) {
	var args rpc.SessionCreateArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	if args.Name == "" {
		s.writeError(req.ID, rpc.ErrInvalidArgs, "name is required")
		return
	}
	s.mu.Lock()
	if _, exists := s.sessions[args.Name]; exists {
		s.mu.Unlock()
		s.writeError(req.ID, rpc.ErrSessionExists, fmt.Sprintf("session %q already exists", args.Name))
		return
	}
	s.mu.Unlock()

	cwd := args.Cwd
	if cwd == "" {
		cwd = "/"
	}
	bash, err := NewBashSession(args.Name, cwd, args.Env)
	if err != nil {
		s.writeError(req.ID, rpc.ErrInternal, err.Error())
		return
	}
	sess := &Session{
		Name:   args.Name,
		Bash:   bash,
		Editor: NewFileEditor(),
		reqs:   make(chan *pendingRequest, 8),
		done:   make(chan struct{}),
	}
	s.mu.Lock()
	s.sessions[args.Name] = sess
	s.mu.Unlock()
	go s.sessionLoop(sess)

	s.writeOK(req.ID, rpc.SessionCreateResult{Name: args.Name, PID: bash.PID()})
}

func (s *Server) handleSessionDestroy(req *rpc.Request) {
	var args rpc.SessionDestroyArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	s.mu.Lock()
	sess, ok := s.sessions[args.Name]
	if ok {
		delete(s.sessions, args.Name)
	}
	s.mu.Unlock()
	if !ok {
		s.writeError(req.ID, rpc.ErrSessionNotFound, args.Name)
		return
	}
	sess.closeReqs()
	<-sess.done
	_ = sess.Bash.Close()
	s.writeOK(req.ID, rpc.SessionDestroyResult{Name: args.Name})
}

func (s *Server) handleSessionList(req *rpc.Request) {
	s.mu.Lock()
	out := rpc.SessionListResult{Sessions: make([]rpc.SessionInfo, 0, len(s.sessions))}
	for _, sess := range s.sessions {
		out.Sessions = append(out.Sessions, rpc.SessionInfo{
			Name: sess.Name,
			PID:  sess.Bash.PID(),
			Cwd:  sess.Bash.Cwd(),
		})
	}
	s.mu.Unlock()
	s.writeOK(req.ID, out)
}

// ---------------- per-session ops ----------------

func (s *Server) sessionLoop(sess *Session) {
	defer close(sess.done)
	for pr := range sess.reqs {
		s.handleSessionOp(sess, &pr.req)
	}
}

func (s *Server) handleSessionOp(sess *Session, req *rpc.Request) {
	switch req.Op {
	case rpc.OpBashExec:
		s.handleBashExec(sess, req)
	case rpc.OpFileView:
		s.handleFileView(sess, req)
	case rpc.OpFileCreate:
		s.handleFileCreate(sess, req)
	case rpc.OpFileStrReplace:
		s.handleFileStrReplace(sess, req)
	case rpc.OpFileInsert:
		s.handleFileInsert(sess, req)
	case rpc.OpFileUndo:
		s.handleFileUndo(sess, req)
	case rpc.OpFileExists:
		s.handleFileExists(sess, req)
	case rpc.OpListDir:
		s.handleListDir(sess, req)
	default:
		s.writeError(req.ID, rpc.ErrUnknownOp, req.Op)
	}
}

func (s *Server) handleBashExec(sess *Session, req *rpc.Request) {
	var args rpc.BashExecArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	timeout := time.Duration(args.TimeoutSeconds) * time.Second
	if timeout <= 0 {
		timeout = 60 * time.Second
	}
	var stream StreamCallback
	if args.Stream {
		stream = func(chunk string) {
			s.writeChunk(req.ID, chunk)
		}
	}
	res, err := sess.Bash.Exec(context.Background(), args.Command, timeout, stream)
	if err != nil {
		s.writeError(req.ID, rpc.ErrBashDead, err.Error())
		return
	}
	s.writeOK(req.ID, rpc.BashExecResult{
		Stdout:   res.Stdout,
		ExitCode: res.ExitCode,
		Cwd:      res.Cwd,
		TimedOut: res.TimedOut,
	})
}

func (s *Server) handleFileView(sess *Session, req *rpc.Request) {
	var args rpc.FileViewArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	content, isDir, err := sess.Editor.View(args.Path, args.ViewRange)
	if err != nil {
		code := rpc.ErrInternal
		msg := err.Error()
		if strings.HasPrefix(msg, "file_not_found:") {
			code = rpc.ErrFileNotFound
		} else if strings.HasPrefix(msg, "file_too_large:") {
			code = rpc.ErrFileTooLarge
		}
		s.writeError(req.ID, code, msg)
		return
	}
	s.writeOK(req.ID, rpc.FileViewResult{Content: content, IsDirectory: isDir})
}

func (s *Server) handleFileCreate(sess *Session, req *rpc.Request) {
	var args rpc.FileCreateArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	content, err := sess.Editor.Create(args.Path, args.FileText)
	if err != nil {
		code := rpc.ErrInternal
		if strings.HasPrefix(err.Error(), "file_exists:") {
			code = rpc.ErrFileExists
		}
		s.writeError(req.ID, code, err.Error())
		return
	}
	s.writeOK(req.ID, rpc.FileCreateResult{Content: content})
}

func (s *Server) handleFileStrReplace(sess *Session, req *rpc.Request) {
	var args rpc.FileStrReplaceArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	content, err := sess.Editor.StrReplace(args.Path, args.OldStr, args.NewStr)
	if err != nil {
		switch {
		case IsStrReplaceNoMatchErr(err):
			s.writeError(req.ID, rpc.ErrStrReplaceNoMatch, content)
		case IsStrReplaceMultipleErr(err):
			s.writeError(req.ID, rpc.ErrStrReplaceMultiple, content)
		default:
			s.writeError(req.ID, rpc.ErrInternal, err.Error())
		}
		return
	}
	s.writeOK(req.ID, rpc.FileStrReplaceResult{Content: content})
}

func (s *Server) handleFileInsert(sess *Session, req *rpc.Request) {
	var args rpc.FileInsertArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	content, err := sess.Editor.Insert(args.Path, args.InsertLine, args.NewStr)
	if err != nil {
		s.writeError(req.ID, rpc.ErrInternal, err.Error())
		return
	}
	s.writeOK(req.ID, rpc.FileInsertResult{Content: content})
}

func (s *Server) handleFileUndo(sess *Session, req *rpc.Request) {
	var args rpc.FileUndoArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	content, err := sess.Editor.Undo(args.Path)
	if err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	s.writeOK(req.ID, rpc.FileUndoResult{Content: content})
}

func (s *Server) handleFileExists(sess *Session, req *rpc.Request) {
	var args rpc.FileExistsArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	exists, isFile, isDir, err := sess.Editor.Exists(args.Path)
	if err != nil {
		s.writeError(req.ID, rpc.ErrInternal, err.Error())
		return
	}
	s.writeOK(req.ID, rpc.FileExistsResult{Exists: exists, IsFile: isFile, IsDir: isDir})
}

func (s *Server) handleListDir(sess *Session, req *rpc.Request) {
	var args rpc.ListDirArgs
	if err := json.Unmarshal(req.Args, &args); err != nil {
		s.writeError(req.ID, rpc.ErrInvalidArgs, err.Error())
		return
	}
	entries, err := sess.Editor.ListDir(args.Path, args.MaxDepth)
	if err != nil {
		s.writeError(req.ID, rpc.ErrInternal, err.Error())
		return
	}
	s.writeOK(req.ID, rpc.ListDirResult{Entries: entries})
}

// ---------------- response writers ----------------

func (s *Server) writeOK(id string, result any) {
	raw, err := json.Marshal(result)
	if err != nil {
		s.writeError(id, rpc.ErrInternal, err.Error())
		return
	}
	resp := rpc.Response{ID: id, OK: true, Result: raw}
	s.writeJSON(resp)
}

func (s *Server) writeError(id, code, msg string) {
	resp := rpc.Response{ID: id, OK: false, Error: &rpc.Error{Code: code, Message: msg}}
	s.writeJSON(resp)
}

func (s *Server) writeChunk(id, chunk string) {
	s.writeJSON(rpc.StreamChunk{ID: id, Stream: true, Chunk: chunk})
}

func (s *Server) writeJSON(v any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	s.outMu.Lock()
	defer s.outMu.Unlock()
	_, _ = s.out.Write(b)
	_, _ = s.out.Write([]byte("\n"))
}

// ---------------- session helpers ----------------

// closeReqs signals the session goroutine that no more requests will be
// sent. The goroutine drains any queued requests, finishes them, and exits.
func (sess *Session) closeReqs() {
	sess.closed.Do(func() { close(sess.reqs) })
}

// sessionIDOf pulls the session_id field out of a raw Args payload. All
// session-scoped ops carry it.
func sessionIDOf(req *rpc.Request) (string, error) {
	var peek struct {
		SessionID string `json:"session_id"`
	}
	if err := json.Unmarshal(req.Args, &peek); err != nil {
		return "", fmt.Errorf("invalid args: %w", err)
	}
	if peek.SessionID == "" {
		return "", fmt.Errorf("session_id is required for op %q", req.Op)
	}
	return peek.SessionID, nil
}
