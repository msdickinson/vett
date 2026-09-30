// Package sandbox is the host-side wrapper around a Docker container and
// its vett-sidecar subprocess. Client owns the stdin/stdout pipes to the
// sidecar and correlates responses by request ID. Docker manages container
// lifecycle via shell-outs to the `docker` CLI.
package sandbox

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/msdickinson/vett/sidecar/pkg/rpc"
)

// Client is a framed RPC client over a subprocess's stdin/stdout.
type Client struct {
	writer *bufio.Writer
	reader *bufio.Scanner
	wMu    sync.Mutex

	pending sync.Map // id -> chan *rpc.Response
	streams sync.Map // id -> chan string

	nextID atomic.Int64

	done chan struct{}
	err  error
	once sync.Once
}

// NewClient wraps the given in/out (typically the sidecar subprocess's
// stdout/stdin) and starts a background goroutine that reads responses.
// Note the input here is what the HOST reads (the sidecar's stdout); the
// output is what the HOST writes (the sidecar's stdin).
func NewClient(sidecarStdout io.Reader, sidecarStdin io.Writer) *Client {
	sc := bufio.NewScanner(sidecarStdout)
	sc.Buffer(make([]byte, 64*1024), 16*1024*1024)
	c := &Client{
		writer: bufio.NewWriter(sidecarStdin),
		reader: sc,
		done:   make(chan struct{}),
	}
	go c.readLoop()
	return c
}

func (c *Client) readLoop() {
	defer c.closeAllPending()
	for c.reader.Scan() {
		line := c.reader.Bytes()
		if len(line) == 0 {
			continue
		}
		// Peek the envelope to figure out if it's a stream chunk or a
		// terminal response.
		var peek struct {
			ID     string `json:"id"`
			Stream bool   `json:"stream"`
		}
		if err := json.Unmarshal(line, &peek); err != nil {
			continue
		}
		if peek.Stream {
			var chunk rpc.StreamChunk
			if err := json.Unmarshal(line, &chunk); err == nil {
				if ch, ok := c.streams.Load(peek.ID); ok {
					select {
					case ch.(chan string) <- chunk.Chunk:
					default:
					}
				}
			}
			continue
		}
		var resp rpc.Response
		if err := json.Unmarshal(line, &resp); err != nil {
			continue
		}
		if ch, ok := c.pending.LoadAndDelete(peek.ID); ok {
			ch.(chan *rpc.Response) <- &resp
		}
	}
	if err := c.reader.Err(); err != nil {
		c.err = err
	}
}

func (c *Client) closeAllPending() {
	c.once.Do(func() { close(c.done) })
	c.pending.Range(func(k, v any) bool {
		ch := v.(chan *rpc.Response)
		select {
		case ch <- &rpc.Response{ID: k.(string), OK: false, Error: &rpc.Error{Code: rpc.ErrInternal, Message: "sidecar stream closed"}}:
		default:
		}
		c.pending.Delete(k)
		return true
	})
}

// Call sends a request and blocks until a terminal response arrives, the
// context is cancelled, or the sidecar stream dies.
func (c *Client) Call(ctx context.Context, op string, args any) (*rpc.Response, error) {
	return c.callWith(ctx, op, args, nil)
}

// CallStream is like Call but delivers stream chunks through the given
// callback as they arrive. The terminal response is returned at the end.
func (c *Client) CallStream(ctx context.Context, op string, args any, onChunk func(string)) (*rpc.Response, error) {
	return c.callWith(ctx, op, args, onChunk)
}

func (c *Client) callWith(ctx context.Context, op string, args any, onChunk func(string)) (*rpc.Response, error) {
	id := "req-" + strconv.FormatInt(c.nextID.Add(1), 10)
	respCh := make(chan *rpc.Response, 1)
	c.pending.Store(id, respCh)
	defer c.pending.Delete(id)

	if onChunk != nil {
		streamCh := make(chan string, 32)
		c.streams.Store(id, streamCh)
		defer c.streams.Delete(id)
		go func() {
			for chunk := range streamCh {
				onChunk(chunk)
			}
		}()
		defer close(streamCh)
	}

	rawArgs, err := json.Marshal(args)
	if err != nil {
		return nil, fmt.Errorf("marshal args: %w", err)
	}
	req := rpc.Request{ID: id, Op: op, Args: rawArgs}
	line, err := json.Marshal(req)
	if err != nil {
		return nil, fmt.Errorf("marshal request: %w", err)
	}
	c.wMu.Lock()
	_, werr := c.writer.Write(line)
	if werr == nil {
		_, werr = c.writer.Write([]byte("\n"))
	}
	if werr == nil {
		werr = c.writer.Flush()
	}
	c.wMu.Unlock()
	if werr != nil {
		return nil, fmt.Errorf("write request: %w", werr)
	}

	select {
	case resp := <-respCh:
		return resp, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-c.done:
		return nil, errors.New("sidecar stream closed")
	}
}

// Hello performs the mandatory version handshake. Must be the first call.
func (c *Client) Hello(ctx context.Context, hostVersion string) (*rpc.HelloResult, error) {
	resp, err := c.Call(ctx, rpc.OpHello, rpc.HelloArgs{
		HostVersion:     hostVersion,
		ProtocolVersion: rpc.ProtocolVersion,
	})
	if err != nil {
		return nil, err
	}
	if !resp.OK {
		return nil, fmt.Errorf("hello failed: %s", resp.Error.Message)
	}
	var out rpc.HelloResult
	if err := json.Unmarshal(resp.Result, &out); err != nil {
		return nil, err
	}
	if out.ProtocolVersion != rpc.ProtocolVersion {
		return nil, fmt.Errorf("protocol version mismatch: host=%d sidecar=%d", rpc.ProtocolVersion, out.ProtocolVersion)
	}
	return &out, nil
}

// Convenience wrappers for the ops the agent loop needs.

func (c *Client) SessionCreate(ctx context.Context, name, cwd string, env map[string]string) error {
	resp, err := c.Call(ctx, rpc.OpSessionCreate, rpc.SessionCreateArgs{Name: name, Cwd: cwd, Env: env})
	return mustOK(resp, err)
}

func (c *Client) SessionDestroy(ctx context.Context, name string) error {
	resp, err := c.Call(ctx, rpc.OpSessionDestroy, rpc.SessionDestroyArgs{Name: name})
	return mustOK(resp, err)
}

func (c *Client) BashExec(ctx context.Context, sessionID, command string, timeout time.Duration, onChunk func(string)) (*rpc.BashExecResult, error) {
	args := rpc.BashExecArgs{
		SessionID:      sessionID,
		Command:        command,
		TimeoutSeconds: int(timeout / time.Second),
		Stream:         onChunk != nil,
	}
	resp, err := c.callWith(ctx, rpc.OpBashExec, args, onChunk)
	if err != nil {
		return nil, err
	}
	if !resp.OK {
		return nil, fmt.Errorf("bash_exec: %s", resp.Error.Message)
	}
	var out rpc.BashExecResult
	if err := json.Unmarshal(resp.Result, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (c *Client) FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (*rpc.FileViewResult, error) {
	resp, err := c.Call(ctx, rpc.OpFileView, rpc.FileViewArgs{SessionID: sessionID, Path: path, ViewRange: viewRange})
	if err != nil {
		return nil, err
	}
	if !resp.OK {
		return nil, fmt.Errorf("file_view: %s", resp.Error.Message)
	}
	var out rpc.FileViewResult
	return &out, json.Unmarshal(resp.Result, &out)
}

func (c *Client) FileCreate(ctx context.Context, sessionID, path, fileText string) (*rpc.FileCreateResult, error) {
	resp, err := c.Call(ctx, rpc.OpFileCreate, rpc.FileCreateArgs{SessionID: sessionID, Path: path, FileText: fileText})
	if err != nil {
		return nil, err
	}
	if !resp.OK {
		return nil, fmt.Errorf("file_create: %s", resp.Error.Message)
	}
	var out rpc.FileCreateResult
	return &out, json.Unmarshal(resp.Result, &out)
}

func (c *Client) FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (*rpc.FileInsertResult, error) {
	resp, err := c.Call(ctx, rpc.OpFileInsert, rpc.FileInsertArgs{
		SessionID: sessionID, Path: path, InsertLine: insertLine, NewStr: newStr,
	})
	if err != nil {
		return nil, err
	}
	if !resp.OK {
		return nil, fmt.Errorf("file_insert: %s", resp.Error.Message)
	}
	var out rpc.FileInsertResult
	return &out, json.Unmarshal(resp.Result, &out)
}

func (c *Client) FileUndo(ctx context.Context, sessionID, path string) (*rpc.FileUndoResult, error) {
	resp, err := c.Call(ctx, rpc.OpFileUndo, rpc.FileUndoArgs{SessionID: sessionID, Path: path})
	if err != nil {
		return nil, err
	}
	if !resp.OK {
		return nil, fmt.Errorf("file_undo: %s", resp.Error.Message)
	}
	var out rpc.FileUndoResult
	return &out, json.Unmarshal(resp.Result, &out)
}

func (c *Client) FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (*rpc.FileStrReplaceResult, string, error) {
	resp, err := c.Call(ctx, rpc.OpFileStrReplace, rpc.FileStrReplaceArgs{SessionID: sessionID, Path: path, OldStr: oldStr, NewStr: newStr})
	if err != nil {
		return nil, "", err
	}
	if !resp.OK {
		// These errors are recoverable — the LLM should see the text.
		if resp.Error.Code == rpc.ErrStrReplaceNoMatch || resp.Error.Code == rpc.ErrStrReplaceMultiple {
			return nil, resp.Error.Message, nil
		}
		return nil, "", fmt.Errorf("file_str_replace: %s", resp.Error.Message)
	}
	var out rpc.FileStrReplaceResult
	return &out, "", json.Unmarshal(resp.Result, &out)
}

func mustOK(resp *rpc.Response, err error) error {
	if err != nil {
		return err
	}
	if !resp.OK {
		return fmt.Errorf("sidecar op failed: %s", resp.Error.Message)
	}
	return nil
}
