// File editor ops are native Go filesystem operations — they never go
// through bash, so no shell escaping is possible. Semantics mirror
// openhands-tools/file_editor/editor.py as described in
// docs/openhands-reference-spec.md §7.
//
// Phase 1b ships the common ops. Byte-exact envelope parity with openhands'
// response strings is refined further in Phase 1c when the file_editor tool
// wrapper lands.

package sidecar

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"unicode/utf8"
)

const (
	// MaxFileSizeBytes is openhands' 10 MB cap.
	MaxFileSizeBytes = 10 * 1024 * 1024
	// MaxResponseLen is the char cap before truncation notices.
	MaxResponseLen = 16000
	// SnippetContext is the number of lines of context shown around an edit.
	SnippetContext = 4
	// TruncatedNotice mirrors openhands-tools utils/constants.py
	// TEXT_FILE_CONTENT_TRUNCATED_NOTICE byte-for-byte.
	TruncatedNotice = "<response clipped><NOTE>Due to the max output limit, only part of this file has been shown to you. You should retry this tool after you have searched inside the file with `grep -n` in order to find the line numbers of what you are looking for.</NOTE>"
)

// FileEditor holds per-session mutable state — specifically the per-file
// undo stack. Native ops have no other hidden state.
type FileEditor struct {
	mu    sync.Mutex
	undo  map[string][]string // path -> stack of prior contents
}

func NewFileEditor() *FileEditor {
	return &FileEditor{undo: map[string][]string{}}
}

// View returns a cat -n-style representation of the file (or a directory
// listing for a directory).
func (e *FileEditor) View(path string, viewRange *[2]int) (content string, isDir bool, err error) {
	info, err := os.Stat(path)
	if err != nil {
		if os.IsNotExist(err) {
			return "", false, errFileNotFound(path)
		}
		return "", false, err
	}
	if info.IsDir() {
		list, derr := listDirShallow(path, 2)
		if derr != nil {
			return "", true, derr
		}
		header := fmt.Sprintf("Here's the files and directories up to 2 levels deep in %s, excluding hidden items:\n", path)
		return truncate(header + strings.Join(list, "\n") + "\n"), true, nil
	}
	if info.Size() > MaxFileSizeBytes {
		return "", false, errFileTooLarge(path, info.Size())
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return "", false, err
	}
	if !utf8.Valid(raw) {
		return "", false, fmt.Errorf("file %s is not valid UTF-8 — binary files are not supported", path)
	}
	text := expandTabs(string(raw), 8)
	lines := strings.Split(text, "\n")
	// Drop the trailing empty line if the file ended with \n so line count
	// matches `cat -n` behavior.
	if n := len(lines); n > 0 && lines[n-1] == "" {
		lines = lines[:n-1]
	}
	start, end := 1, len(lines)
	if viewRange != nil {
		start = viewRange[0]
		end = viewRange[1]
		if start < 1 {
			start = 1
		}
		if end == -1 || end > len(lines) {
			end = len(lines)
		}
		if start > end {
			return "", false, fmt.Errorf("invalid view_range: start %d > end %d", start, end)
		}
	}
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("Here's the result of running `cat -n` on %s:\n", path))
	for i := start; i <= end; i++ {
		sb.WriteString(fmt.Sprintf("%6d\t%s\n", i, lines[i-1]))
	}
	return truncate(sb.String()), false, nil
}

// Create writes a brand-new file. Fails if the file already exists.
func (e *FileEditor) Create(path, fileText string) (string, error) {
	if _, err := os.Stat(path); err == nil {
		return "", errFileExists(path)
	} else if !os.IsNotExist(err) {
		return "", err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return "", err
	}
	if err := os.WriteFile(path, []byte(fileText), 0o644); err != nil {
		return "", err
	}
	return fmt.Sprintf("File created successfully at: %s", path), nil
}

// StrReplace performs an exact-match substring replacement. Rejects zero
// matches and multiple matches with line-numbered errors.
func (e *FileEditor) StrReplace(path, oldStr, newStr string) (string, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return "", errFileNotFound(path)
		}
		return "", err
	}
	if !utf8.Valid(raw) {
		return "", fmt.Errorf("file %s is not valid UTF-8", path)
	}
	text := expandTabs(string(raw), 8)
	oldExpanded := expandTabs(oldStr, 8)
	occurrences := strings.Count(text, oldExpanded)
	if occurrences == 0 {
		return fmt.Sprintf("No replacement was performed, old_str `%s` did not appear verbatim in %s.", oldStr, path), errStrReplaceNoMatch
	}
	if occurrences > 1 {
		lines := strings.Split(text, "\n")
		var matchLines []int
		for i, line := range lines {
			if strings.Contains(line, oldExpanded) {
				matchLines = append(matchLines, i+1)
			}
		}
		return fmt.Sprintf("No replacement was performed. Multiple occurrences of old_str `%s` in %s at lines %v. Please ensure it is unique.", oldStr, path, matchLines), errStrReplaceMultiple
	}

	e.mu.Lock()
	e.undo[path] = append(e.undo[path], string(raw))
	e.mu.Unlock()

	newText := strings.Replace(text, oldExpanded, expandTabs(newStr, 8), 1)
	if err := os.WriteFile(path, []byte(newText), 0o644); err != nil {
		return "", err
	}
	// Build a snippet centered on the replacement.
	replStart := strings.Index(text, oldExpanded)
	preLines := strings.Count(text[:replStart], "\n")
	snippet := makeSnippet(newText, preLines, preLines+strings.Count(expandTabs(newStr, 8), "\n"))
	return fmt.Sprintf("The file %s has been edited. Here's the result of running `cat -n` on a snippet:\n%s", path, snippet), nil
}

// Insert inserts newStr after the given 1-indexed line number.
func (e *FileEditor) Insert(path string, afterLine int, newStr string) (string, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return "", errFileNotFound(path)
		}
		return "", err
	}
	if !utf8.Valid(raw) {
		return "", fmt.Errorf("file %s is not valid UTF-8", path)
	}
	text := expandTabs(string(raw), 8)
	lines := strings.Split(text, "\n")
	// Split treats "a\n" as ["a",""]; drop that trailing empty to align 1-indexed insertion.
	trailingNewline := strings.HasSuffix(text, "\n")
	if trailingNewline && len(lines) > 0 && lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	if afterLine < 0 || afterLine > len(lines) {
		return "", fmt.Errorf("insert_line %d out of range (file has %d lines)", afterLine, len(lines))
	}
	e.mu.Lock()
	e.undo[path] = append(e.undo[path], string(raw))
	e.mu.Unlock()

	newLines := expandTabs(newStr, 8)
	newLinesArr := strings.Split(strings.TrimRight(newLines, "\n"), "\n")
	result := append([]string{}, lines[:afterLine]...)
	result = append(result, newLinesArr...)
	result = append(result, lines[afterLine:]...)
	out := strings.Join(result, "\n")
	if trailingNewline {
		out += "\n"
	}
	if err := os.WriteFile(path, []byte(out), 0o644); err != nil {
		return "", err
	}
	snippet := makeSnippet(out, afterLine, afterLine+len(newLinesArr)-1)
	return fmt.Sprintf("The file %s has been edited. Here's the result of running `cat -n` on a snippet:\n%s", path, snippet), nil
}

// Undo pops the last edit to the given file.
func (e *FileEditor) Undo(path string) (string, error) {
	e.mu.Lock()
	stack := e.undo[path]
	if len(stack) == 0 {
		e.mu.Unlock()
		return "", fmt.Errorf("no edit history for %s", path)
	}
	prev := stack[len(stack)-1]
	e.undo[path] = stack[:len(stack)-1]
	e.mu.Unlock()
	if err := os.WriteFile(path, []byte(prev), 0o644); err != nil {
		return "", err
	}
	return fmt.Sprintf("Last edit to %s undone successfully.", path), nil
}

// Exists returns existence + file/dir info.
func (e *FileEditor) Exists(path string) (exists, isFile, isDir bool, err error) {
	info, err := os.Stat(path)
	if err != nil {
		if os.IsNotExist(err) {
			return false, false, false, nil
		}
		return false, false, false, err
	}
	return true, info.Mode().IsRegular(), info.IsDir(), nil
}

// ListDir walks path up to maxDepth and returns matching entries.
func (e *FileEditor) ListDir(path string, maxDepth int) ([]string, error) {
	if maxDepth <= 0 {
		maxDepth = 2
	}
	return listDirShallow(path, maxDepth)
}

// ---------------- helpers ----------------

// expandTabs replicates Python str.expandtabs(tabstop). For tabstop=8 this
// matches openhands' behavior on every byte.
func expandTabs(s string, tabstop int) string {
	if !strings.ContainsRune(s, '\t') {
		return s
	}
	var sb strings.Builder
	col := 0
	for _, r := range s {
		switch r {
		case '\t':
			spaces := tabstop - (col % tabstop)
			for i := 0; i < spaces; i++ {
				sb.WriteByte(' ')
			}
			col += spaces
		case '\n':
			sb.WriteRune(r)
			col = 0
		default:
			sb.WriteRune(r)
			col++
		}
	}
	return sb.String()
}

// makeSnippet returns a "cat -n"-style snippet of text centered on the given
// line range [startLine, endLine] (0-indexed), padded with SnippetContext
// lines on each side.
func makeSnippet(text string, startLine, endLine int) string {
	lines := strings.Split(text, "\n")
	if n := len(lines); n > 0 && lines[n-1] == "" {
		lines = lines[:n-1]
	}
	lo := startLine - SnippetContext
	if lo < 0 {
		lo = 0
	}
	hi := endLine + SnippetContext
	if hi >= len(lines) {
		hi = len(lines) - 1
	}
	var sb strings.Builder
	for i := lo; i <= hi; i++ {
		sb.WriteString(fmt.Sprintf("%6d\t%s\n", i+1, lines[i]))
	}
	return sb.String()
}

// truncate enforces MaxResponseLen with the canonical notice.
func truncate(s string) string {
	if len(s) <= MaxResponseLen {
		return s
	}
	return s[:MaxResponseLen] + "\n" + TruncatedNotice
}

// listDirShallow returns a slice of paths rooted at dir, up to maxDepth,
// excluding hidden entries (dotfiles).
func listDirShallow(dir string, maxDepth int) ([]string, error) {
	var out []string
	rootInfo, err := os.Stat(dir)
	if err != nil {
		return nil, err
	}
	if !rootInfo.IsDir() {
		return nil, fmt.Errorf("%s is not a directory", dir)
	}
	out = append(out, dir)
	err = filepath.WalkDir(dir, func(path string, d os.DirEntry, werr error) error {
		if werr != nil {
			return werr
		}
		if path == dir {
			return nil
		}
		rel, _ := filepath.Rel(dir, path)
		depth := len(strings.Split(filepath.ToSlash(rel), "/"))
		if depth > maxDepth {
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if strings.HasPrefix(d.Name(), ".") {
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		out = append(out, filepath.ToSlash(path))
		return nil
	})
	if err != nil {
		return nil, err
	}
	return out, nil
}

// ---------------- sentinels ----------------

var (
	errStrReplaceNoMatch  = errors.New("str_replace_no_match")
	errStrReplaceMultiple = errors.New("str_replace_multi_match")
)

func errFileNotFound(path string) error {
	return fmt.Errorf("file_not_found: %s", path)
}
func errFileExists(path string) error {
	return fmt.Errorf("file_exists: %s", path)
}
func errFileTooLarge(path string, size int64) error {
	return fmt.Errorf("file_too_large: %s (%d bytes)", path, size)
}

// IsStrReplaceNoMatchErr returns true if err is the sentinel.
func IsStrReplaceNoMatchErr(err error) bool    { return errors.Is(err, errStrReplaceNoMatch) }
func IsStrReplaceMultipleErr(err error) bool   { return errors.Is(err, errStrReplaceMultiple) }
