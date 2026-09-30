using Vett.Cli;

namespace Vett.Tests;

/// <summary>
/// Covers the pure-text helpers in <see cref="EditCommand"/>:
/// stdin JSON parsing (snake_case + camelCase), prompt construction,
/// and fenced-block extraction. The LLM round-trip itself isn't
/// exercised here — that needs a stubbed IChatClient and is covered
/// indirectly by the existing AgentTests harness pattern; the inline-
/// edit path is small enough that the helpers are the contract worth
/// pinning down.
/// </summary>
public class EditCommandTests
{
    [Fact]
    public void ParseRequest_snake_case_round_trips()
    {
        var json = """
        {
          "instruction": "extract into a function",
          "selection": "x + 1",
          "selection_start_line": 10,
          "selection_end_line": 12,
          "file_content": "let x = 5;\nx + 1;\n",
          "language": "typescript",
          "file_path": "src/foo.ts"
        }
        """;
        var req = EditCommand.ParseRequest(json);

        Assert.Equal("extract into a function", req.Instruction);
        Assert.Equal("x + 1", req.Selection);
        Assert.Equal(10, req.SelectionStartLine);
        Assert.Equal(12, req.SelectionEndLine);
        Assert.Equal("let x = 5;\nx + 1;\n", req.FileContent);
        Assert.Equal("typescript", req.Language);
        Assert.Equal("src/foo.ts", req.FilePath);
    }

    [Fact]
    public void ParseRequest_camelCase_aliases_work()
    {
        var json = """
        {
          "instruction": "x",
          "selection": "y",
          "selectionStartLine": 1,
          "selectionEndLine": 2,
          "fileContent": "f",
          "filePath": "p"
        }
        """;
        var req = EditCommand.ParseRequest(json);
        Assert.Equal(1, req.SelectionStartLine);
        Assert.Equal(2, req.SelectionEndLine);
        Assert.Equal("f", req.FileContent);
        Assert.Equal("p", req.FilePath);
    }

    [Fact]
    public void ParseRequest_missing_optional_fields_default_safely()
    {
        var json = """{"instruction":"x","selection":"y"}""";
        var req = EditCommand.ParseRequest(json);
        Assert.Null(req.SelectionStartLine);
        Assert.Null(req.SelectionEndLine);
        Assert.Equal("", req.FileContent);
        Assert.Equal("", req.Language);
        Assert.Equal("", req.FilePath);
    }

    [Fact]
    public void BuildSystemPrompt_forbids_fences_and_preambles()
    {
        var p = EditCommand.BuildSystemPrompt();
        Assert.Contains("Output ONLY the replacement code", p);
        Assert.Contains("Do NOT wrap your output in a fenced code block", p);
        Assert.Contains("No preamble", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserPrompt_includes_instruction_file_and_selection_blocks()
    {
        var req = new EditRequest
        {
            Instruction = "rename foo to bar",
            Selection = "foo()",
            FileContent = "function foo() {}\nfoo()\n",
            Language = "typescript",
            FilePath = "x.ts",
            SelectionStartLine = 2,
            SelectionEndLine = 2,
        };
        var u = EditCommand.BuildUserPrompt(req);

        Assert.StartsWith("INSTRUCTION:", u);
        Assert.Contains("rename foo to bar", u);
        Assert.Contains("FILE: x.ts", u);
        Assert.Contains("LANGUAGE: typescript", u);
        Assert.Contains("SELECTION SPAN: lines 2-2", u);
        Assert.Contains("--- FULL FILE (for context) ---", u);
        Assert.Contains("function foo() {}", u);
        Assert.Contains("--- SELECTION TO REPLACE ---", u);
        Assert.Contains("foo()", u);
    }

    [Fact]
    public void BuildUserPrompt_omits_optional_headers_when_blank()
    {
        var req = new EditRequest
        {
            Instruction = "x",
            Selection = "y",
            FileContent = "z",
        };
        var u = EditCommand.BuildUserPrompt(req);
        Assert.DoesNotContain("FILE: ", u);
        Assert.DoesNotContain("LANGUAGE: ", u);
        Assert.DoesNotContain("SELECTION SPAN:", u);
    }

    [Fact]
    public void ExtractCode_strips_fenced_block_with_language_tag()
    {
        var raw = "Here you go:\n```typescript\nconst x = 1;\nconst y = 2;\n```\nThat's it.";
        Assert.Equal("const x = 1;\nconst y = 2;", EditCommand.ExtractCode(raw));
    }

    [Fact]
    public void ExtractCode_strips_fenced_block_without_language_tag()
    {
        var raw = "```\nfoo\nbar\n```";
        Assert.Equal("foo\nbar", EditCommand.ExtractCode(raw));
    }

    [Fact]
    public void ExtractCode_handles_crlf_line_endings()
    {
        var raw = "```ts\r\nfoo\r\nbar\r\n```";
        Assert.Equal("foo\r\nbar", EditCommand.ExtractCode(raw));
    }

    [Fact]
    public void ExtractCode_returns_first_block_when_multiple_present()
    {
        var raw = "```\na\n```\n```\nb\n```";
        Assert.Equal("a", EditCommand.ExtractCode(raw));
    }

    [Fact]
    public void ExtractCode_falls_back_to_raw_when_no_fence()
    {
        var raw = "const x = 1;";
        Assert.Equal("const x = 1;", EditCommand.ExtractCode(raw));
    }

    [Fact]
    public void ExtractCode_trims_one_leading_and_trailing_newline_when_no_fence()
    {
        Assert.Equal("foo", EditCommand.ExtractCode("\nfoo\n"));
        Assert.Equal("foo", EditCommand.ExtractCode("\r\nfoo\r\n"));
    }

    [Fact]
    public void ExtractCode_preserves_leading_indentation_in_block()
    {
        // Important: inline-edit needs the indent to round-trip into the
        // user's file. Don't strip leading spaces.
        var raw = "```ts\n    const x = 1;\n    const y = 2;\n```";
        Assert.Equal("    const x = 1;\n    const y = 2;", EditCommand.ExtractCode(raw));
    }

    [Fact]
    public void ExtractCode_empty_input_round_trips()
    {
        Assert.Equal("", EditCommand.ExtractCode(""));
    }
}
