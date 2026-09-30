package sidecar

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func writeTemp(t *testing.T, content string) string {
	t.Helper()
	dir := t.TempDir()
	p := filepath.Join(dir, "file.py")
	if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
		t.Fatalf("write temp: %v", err)
	}
	return p
}

func TestFileEditorViewFile(t *testing.T) {
	p := writeTemp(t, "def foo():\n    return 1\n")
	e := NewFileEditor()
	content, isDir, err := e.View(p, nil)
	if err != nil {
		t.Fatalf("view: %v", err)
	}
	if isDir {
		t.Fatal("expected file, got directory")
	}
	if !strings.Contains(content, "Here's the result of running `cat -n` on") {
		t.Errorf("missing cat -n header:\n%s", content)
	}
	if !strings.Contains(content, "     1\tdef foo():") {
		t.Errorf("line 1 formatting wrong:\n%s", content)
	}
	if !strings.Contains(content, "     2\t    return 1") {
		t.Errorf("line 2 formatting wrong:\n%s", content)
	}
}

func TestFileEditorCreateFailsIfExists(t *testing.T) {
	p := writeTemp(t, "x")
	e := NewFileEditor()
	if _, err := e.Create(p, "y"); err == nil {
		t.Fatal("expected file_exists error, got nil")
	} else if !strings.HasPrefix(err.Error(), "file_exists:") {
		t.Errorf("wrong error: %v", err)
	}
}

func TestFileEditorCreateSucceeds(t *testing.T) {
	p := filepath.Join(t.TempDir(), "new.txt")
	e := NewFileEditor()
	msg, err := e.Create(p, "hello\n")
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if !strings.Contains(msg, "File created successfully") {
		t.Errorf("unexpected create message: %q", msg)
	}
	body, _ := os.ReadFile(p)
	if string(body) != "hello\n" {
		t.Errorf("file body mismatch: %q", string(body))
	}
}

func TestFileEditorStrReplaceSingleMatch(t *testing.T) {
	p := writeTemp(t, "def foo():\n    return 1\n")
	e := NewFileEditor()
	msg, err := e.StrReplace(p, "return 1", "return 2")
	if err != nil {
		t.Fatalf("str_replace: %v (msg=%q)", err, msg)
	}
	if !strings.Contains(msg, "has been edited") {
		t.Errorf("missing edited message: %q", msg)
	}
	body, _ := os.ReadFile(p)
	if string(body) != "def foo():\n    return 2\n" {
		t.Errorf("file body mismatch: %q", string(body))
	}
}

func TestFileEditorStrReplaceNoMatch(t *testing.T) {
	p := writeTemp(t, "def foo():\n    return 1\n")
	e := NewFileEditor()
	msg, err := e.StrReplace(p, "nonexistent", "x")
	if !IsStrReplaceNoMatchErr(err) {
		t.Fatalf("expected no_match sentinel, got %v", err)
	}
	if !strings.Contains(msg, "did not appear verbatim") {
		t.Errorf("unexpected no-match message: %q", msg)
	}
}

func TestFileEditorStrReplaceMultipleMatches(t *testing.T) {
	p := writeTemp(t, "x = 1\nx = 1\n")
	e := NewFileEditor()
	msg, err := e.StrReplace(p, "x = 1", "x = 2")
	if !IsStrReplaceMultipleErr(err) {
		t.Fatalf("expected multiple sentinel, got %v", err)
	}
	if !strings.Contains(msg, "Multiple occurrences") {
		t.Errorf("unexpected multi-match message: %q", msg)
	}
	if !strings.Contains(msg, "[1 2]") {
		t.Errorf("expected line-number list in message: %q", msg)
	}
}

func TestFileEditorUndo(t *testing.T) {
	p := writeTemp(t, "def foo():\n    return 1\n")
	e := NewFileEditor()
	if _, err := e.StrReplace(p, "return 1", "return 2"); err != nil {
		t.Fatalf("str_replace: %v", err)
	}
	if _, err := e.Undo(p); err != nil {
		t.Fatalf("undo: %v", err)
	}
	body, _ := os.ReadFile(p)
	if string(body) != "def foo():\n    return 1\n" {
		t.Errorf("undo didn't restore body: %q", string(body))
	}
}

func TestFileEditorInsert(t *testing.T) {
	p := writeTemp(t, "line 1\nline 2\nline 3\n")
	e := NewFileEditor()
	if _, err := e.Insert(p, 2, "inserted\n"); err != nil {
		t.Fatalf("insert: %v", err)
	}
	body, _ := os.ReadFile(p)
	want := "line 1\nline 2\ninserted\nline 3\n"
	if string(body) != want {
		t.Errorf("insert result mismatch:\n  got:  %q\n  want: %q", string(body), want)
	}
}

func TestFileEditorExpandTabsMatchesPython(t *testing.T) {
	// Python: "a\tb".expandtabs(8) -> "a       b" (7 spaces to column 8)
	if got := expandTabs("a\tb", 8); got != "a       b" {
		t.Errorf("expandtabs mismatch: got %q want %q", got, "a       b")
	}
	// "ab\tc" -> "ab      c"
	if got := expandTabs("ab\tc", 8); got != "ab      c" {
		t.Errorf("expandtabs mismatch: got %q want %q", got, "ab      c")
	}
	// tab after newline resets column
	if got := expandTabs("line1\n\tx", 8); got != "line1\n        x" {
		t.Errorf("expandtabs mismatch: got %q want %q", got, "line1\n        x")
	}
}

func TestFileEditorViewDirectory(t *testing.T) {
	dir := t.TempDir()
	_ = os.WriteFile(filepath.Join(dir, "a.py"), []byte("x"), 0o644)
	_ = os.WriteFile(filepath.Join(dir, ".hidden"), []byte("y"), 0o644)
	_ = os.Mkdir(filepath.Join(dir, "sub"), 0o755)
	_ = os.WriteFile(filepath.Join(dir, "sub", "b.py"), []byte("z"), 0o644)

	e := NewFileEditor()
	content, isDir, err := e.View(dir, nil)
	if err != nil {
		t.Fatalf("view dir: %v", err)
	}
	if !isDir {
		t.Fatal("expected directory, got file")
	}
	if !strings.Contains(content, "excluding hidden items") {
		t.Errorf("missing directory header:\n%s", content)
	}
	if !strings.Contains(content, "a.py") || !strings.Contains(content, "b.py") {
		t.Errorf("missing expected entries:\n%s", content)
	}
	if strings.Contains(content, ".hidden") {
		t.Errorf("hidden file should have been skipped:\n%s", content)
	}
}

func TestFileEditorFileExists(t *testing.T) {
	p := writeTemp(t, "x")
	e := NewFileEditor()
	exists, isFile, isDir, err := e.Exists(p)
	if err != nil {
		t.Fatalf("exists: %v", err)
	}
	if !exists || !isFile || isDir {
		t.Errorf("expected (true,true,false), got (%v,%v,%v)", exists, isFile, isDir)
	}
	exists, _, _, err = e.Exists(filepath.Join(t.TempDir(), "nope"))
	if err != nil {
		t.Fatalf("exists on missing: %v", err)
	}
	if exists {
		t.Error("expected missing file to return exists=false")
	}
}
