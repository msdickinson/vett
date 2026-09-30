using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// Covers the per-language extractors + the workspace-walk + render
/// path. Each test scaffolds a tiny temp directory tree and asserts
/// the rendered map contains (or omits) what we expect.
/// </summary>
public class RepoMapTests : IDisposable
{
    private readonly string _tmp;

    public RepoMapTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "vett-repomap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ExtractSignatures_TypeScript_FindsCommonShapes()
    {
        var src = @"
export function foo(x: number): string { return ''; }
export class Bar {
  baz() {}
}
export interface Qux { a: number; }
export type Alias = string;
export const arrow = (n: number) => n * 2;
function privateHelper() {}
";
        var sigs = RepoMap.ExtractSignatures(src, "typescript");
        Assert.Contains(sigs, s => s.Contains("function foo"));
        Assert.Contains(sigs, s => s.Contains("class Bar"));
        Assert.Contains(sigs, s => s.Contains("interface Qux"));
        Assert.Contains(sigs, s => s.Contains("type Alias"));
        Assert.Contains(sigs, s => s.Contains("const arrow"));
        // Helper without `export` STILL matches the bare function
        // pattern — that's deliberate (file-level non-exported
        // top-level symbols are still useful structure).
        Assert.Contains(sigs, s => s.Contains("function privateHelper"));
    }

    [Fact]
    public void ExtractSignatures_Python_FindsDefAndClass_SkipsDeepIndents()
    {
        var src = @"
def top_level(x):
    pass

class Foo:
    def method(self):
        pass

class Bar(Foo):
    def __init__(self, n: int) -> None:
        # nested helper at 8-space indent — should NOT be picked up
        def helper():
            pass
        self.n = n
";
        var sigs = RepoMap.ExtractSignatures(src, "python");
        Assert.Contains(sigs, s => s.Contains("def top_level"));
        Assert.Contains(sigs, s => s.Contains("class Foo"));
        Assert.Contains(sigs, s => s.Contains("def method"));
        Assert.Contains(sigs, s => s.Contains("class Bar(Foo)"));
        Assert.Contains(sigs, s => s.Contains("def __init__"));
        Assert.DoesNotContain(sigs, s => s.Contains("def helper"));
    }

    [Fact]
    public void ExtractSignatures_CSharp_FindsClassesAndPublicMethods()
    {
        var src = @"
namespace Foo;

public sealed class Bar
{
    public int Baz(string s) => s.Length;
    public static async Task<int> Quux() { return 1; }
    private void hidden() {}
}

public interface IThing
{
    void DoIt();
}
";
        var sigs = RepoMap.ExtractSignatures(src, "csharp");
        Assert.Contains(sigs, s => s.Contains("class Bar"));
        Assert.Contains(sigs, s => s.Contains("interface IThing"));
        Assert.Contains(sigs, s => s.Contains("Baz(string s)"));
    }

    [Fact]
    public void ExtractSignatures_Go_FindsFuncAndType()
    {
        var src = @"
package foo

func Bar(x int) error {
    return nil
}

func (r *Receiver) Method() {}

type MyStruct struct {
    Field int
}

type MyIface interface {
    Do()
}
";
        var sigs = RepoMap.ExtractSignatures(src, "go");
        Assert.Contains(sigs, s => s.Contains("func Bar"));
        Assert.Contains(sigs, s => s.Contains("func (r *Receiver) Method"));
        Assert.Contains(sigs, s => s.Contains("type MyStruct struct"));
        Assert.Contains(sigs, s => s.Contains("type MyIface interface"));
    }

    [Fact]
    public void ExtractSignatures_Rust_FindsFnStructTraitImpl()
    {
        var src = @"
pub fn foo(x: i32) -> i32 { x + 1 }

pub struct Bar { field: u32 }

pub trait MyTrait {
    fn do_it(&self);
}

impl MyTrait for Bar {
    fn do_it(&self) {}
}
";
        var sigs = RepoMap.ExtractSignatures(src, "rust");
        Assert.Contains(sigs, s => s.Contains("pub fn foo"));
        Assert.Contains(sigs, s => s.Contains("pub struct Bar"));
        Assert.Contains(sigs, s => s.Contains("pub trait MyTrait"));
        Assert.Contains(sigs, s => s.Contains("impl MyTrait for Bar"));
    }

    [Fact]
    public void ExtractSignatures_CapsAtTwelvePerFile()
    {
        // Generate 20 functions in one file — extractor caps at 12.
        var lines = Enumerable.Range(0, 20).Select(i => $"export function f{i}() {{}}");
        var src = string.Join("\n", lines);
        var sigs = RepoMap.ExtractSignatures(src, "typescript");
        Assert.Equal(12, sigs.Count);
    }

    [Fact]
    public void Build_WalksWorkspace_RanksByDensity_RendersBlock()
    {
        // Two files: one with 3 sigs, one with 1. Expect the dense one
        // to appear FIRST in the rendered output.
        File.WriteAllText(Path.Combine(_tmp, "dense.ts"),
            "export function a() {}\nexport function b() {}\nexport function c() {}\n");
        File.WriteAllText(Path.Combine(_tmp, "sparse.ts"),
            "export function only_one() {}\n");

        var rendered = RepoMap.Build(_tmp);
        Assert.Contains("dense.ts", rendered);
        Assert.Contains("sparse.ts", rendered);
        // Dense file comes first.
        var denseIdx = rendered.IndexOf("dense.ts", StringComparison.Ordinal);
        var sparseIdx = rendered.IndexOf("sparse.ts", StringComparison.Ordinal);
        Assert.True(denseIdx < sparseIdx);
    }

    [Fact]
    public void Build_SkipsExcludedDirs()
    {
        // node_modules content should NOT appear in the map.
        Directory.CreateDirectory(Path.Combine(_tmp, "node_modules"));
        File.WriteAllText(Path.Combine(_tmp, "node_modules", "vendor.ts"),
            "export function vendored() {}\n");
        File.WriteAllText(Path.Combine(_tmp, "src.ts"),
            "export function mine() {}\n");

        var rendered = RepoMap.Build(_tmp);
        Assert.Contains("mine", rendered);
        Assert.DoesNotContain("vendored", rendered);
    }

    [Fact]
    public void Build_RespectsExtraExcludes()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "experiments"));
        File.WriteAllText(Path.Combine(_tmp, "experiments", "scratch.ts"),
            "export function scratch_fn() {}\n");
        File.WriteAllText(Path.Combine(_tmp, "src.ts"),
            "export function real_fn() {}\n");

        var rendered = RepoMap.Build(_tmp, extraExcludeDirs: new[] { "experiments" });
        Assert.Contains("real_fn", rendered);
        Assert.DoesNotContain("scratch_fn", rendered);
    }

    [Fact]
    public void Build_RespectsMaxCharsBudget()
    {
        // Generate enough files to exceed any tiny budget.
        for (int i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(_tmp, $"f{i}.ts"),
                $"export function only{i}() {{}}\n");
        }
        var rendered = RepoMap.Build(_tmp, maxChars: 200);
        // Tight budget should produce a truncation marker.
        Assert.Contains("truncated", rendered);
        // ...but still produce some content.
        Assert.True(rendered.Length > 0);
    }

    [Fact]
    public void Build_NoSourceFiles_ReturnsEmptyString()
    {
        File.WriteAllText(Path.Combine(_tmp, "README.md"), "# nothing source-y");
        var rendered = RepoMap.Build(_tmp);
        Assert.Empty(rendered);
    }

    [Fact]
    public void Apply_EmptyMap_LeavesSystemPromptUnchanged()
    {
        var prompt = "You are a helpful coding assistant.";
        var result = RepoMap.Apply(prompt, _tmp); // empty workspace → no map
        Assert.Equal(prompt, result);
    }

    [Fact]
    public void Apply_NonEmptyMap_PrependsRepoMapEnvelope()
    {
        File.WriteAllText(Path.Combine(_tmp, "src.ts"), "export function fn() {}\n");
        var prompt = "You are a helpful coding assistant.";
        var result = RepoMap.Apply(prompt, _tmp);
        Assert.StartsWith("<repo_map>", result);
        Assert.Contains("</repo_map>", result);
        Assert.Contains("src.ts", result);
        Assert.EndsWith(prompt, result);
    }

    [Fact]
    public void Build_SkipsHugeFiles()
    {
        // Write a >256KB file; should be skipped despite having a valid sig.
        var big = "export function huge() {}\n" + new string('x', 300_000);
        File.WriteAllText(Path.Combine(_tmp, "big.ts"), big);
        File.WriteAllText(Path.Combine(_tmp, "small.ts"),
            "export function small_fn() {}\n");

        var rendered = RepoMap.Build(_tmp);
        Assert.Contains("small_fn", rendered);
        Assert.DoesNotContain("huge", rendered);
    }

    [Fact]
    public void Build_NonExistentRoot_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, RepoMap.Build("/no/such/path/exists"));
    }

    // --- #14 close-out: PageRank-style References ranking ---------

    [Fact]
    public void ExtractSymbolName_extracts_canonical_names_from_signatures()
    {
        Assert.Equal("foo", RepoMap.ExtractSymbolName("function foo(arg: string)"));
        Assert.Equal("Bar", RepoMap.ExtractSymbolName("class Bar<T> extends Base"));
        Assert.Equal("IUser", RepoMap.ExtractSymbolName("interface IUser"));
        Assert.Equal("doThing", RepoMap.ExtractSymbolName("public async doThing(): Promise<void>"));
        Assert.Equal("compute", RepoMap.ExtractSymbolName("def compute(self, x):"));
        Assert.Equal("baz", RepoMap.ExtractSymbolName("pub fn baz(x: u32) -> u32"));
    }

    [Fact]
    public void ExtractSymbolName_falls_back_to_first_long_identifier()
    {
        // Decorator-only signature line — no keyword to match. Falls
        // back to first identifier ≥ 3 chars.
        Assert.Equal("decorated", RepoMap.ExtractSymbolName("@something\ndecorated method"));
    }

    [Fact]
    public void ExtractSymbolName_returns_empty_on_garbage()
    {
        Assert.Equal(string.Empty, RepoMap.ExtractSymbolName(""));
        Assert.Equal(string.Empty, RepoMap.ExtractSymbolName("   "));
        // No 3+ char identifier in the input.
        Assert.Equal(string.Empty, RepoMap.ExtractSymbolName("a b c"));
    }

    [Fact]
    public void CountWordOccurrence_respects_word_boundaries()
    {
        // `useFoo` should NOT match `foo` even though `foo` is a substring.
        Assert.Equal(0, RepoMap.CountWordOccurrence("import { useFoo } from './x'", "foo"));
        // `foo()` matches `foo` (parens don't extend an identifier).
        Assert.Equal(1, RepoMap.CountWordOccurrence("foo()", "foo"));
        // `foo_bar` does NOT match `foo` (underscore is identifier-continuing).
        Assert.Equal(0, RepoMap.CountWordOccurrence("foo_bar", "foo"));
        // Empty inputs.
        Assert.Equal(0, RepoMap.CountWordOccurrence("", "foo"));
        Assert.Equal(0, RepoMap.CountWordOccurrence("foo", ""));
    }

    [Fact]
    public void Build_References_ranking_promotes_widely_referenced_files()
    {
        // util.ts defines a single helper (`tinyHelper`); leaf*.ts
        // each import + use it. With Density ranking util.ts (1 sig)
        // would lose to a leaf with multiple sigs. With References
        // ranking the cross-file references should boost util.ts.
        File.WriteAllText(Path.Combine(_tmp, "util.ts"),
            "export function tinyHelper(x: number) { return x + 1; }\n");
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(_tmp, $"leaf{i}.ts"),
                "import { tinyHelper } from './util';\n" +
                $"export function localFunc{i}() {{ return tinyHelper({i}); }}\n");
        }

        var rendered = RepoMap.Build(_tmp, ranking: RepoMap.RepoMapRanking.References);
        // util.ts referenced by 3 leaves → score = 1 + 3 = 4
        // each leaf has 1 sig + (maybe) 0 refs (no one references localFuncN)
        // → util.ts should win.
        Assert.Contains("util.ts", rendered);
        // The (refs: 3) annotation surfaces the ranking signal so the
        // agent sees why the order came out this way.
        Assert.Contains("(refs: 3)", rendered);
        // util.ts should appear before leaf0.ts in the output.
        var utilIdx = rendered.IndexOf("util.ts", StringComparison.Ordinal);
        var leaf0Idx = rendered.IndexOf("leaf0.ts", StringComparison.Ordinal);
        Assert.True(utilIdx < leaf0Idx, "util.ts should rank above leaf0.ts under References ranking");
    }

    [Fact]
    public void Build_Density_ranking_keeps_session_12_v1_behavior()
    {
        // Same setup as the References test, but with Density ranking
        // util.ts (1 sig) loses to a multi-sig leaf. Pinning the v1
        // behavior so callers asking for `density` get exactly that.
        File.WriteAllText(Path.Combine(_tmp, "util.ts"),
            "export function tinyHelper(x: number) { return x + 1; }\n");
        File.WriteAllText(Path.Combine(_tmp, "dense.ts"),
            "export function a() {}\nexport function b() {}\nexport function c() {}\n");

        var rendered = RepoMap.Build(_tmp, ranking: RepoMap.RepoMapRanking.Density);
        var utilIdx = rendered.IndexOf("util.ts", StringComparison.Ordinal);
        var denseIdx = rendered.IndexOf("dense.ts", StringComparison.Ordinal);
        Assert.True(denseIdx < utilIdx, "Density ranking puts dense.ts (3 sigs) above util.ts (1 sig)");
        // No (refs: ...) annotation in Density mode.
        Assert.DoesNotContain("(refs:", rendered);
    }

    [Fact]
    public void Build_References_doesnt_count_self_references()
    {
        // A file that uses its OWN symbol shouldn't get a boost from
        // that — only OTHER files count.
        File.WriteAllText(Path.Combine(_tmp, "self.ts"),
            "export function selfRef() { return selfRef(); }\n");
        var rendered = RepoMap.Build(_tmp, ranking: RepoMap.RepoMapRanking.References);
        // No annotation since refs == 0.
        Assert.DoesNotContain("(refs:", rendered);
    }
}
