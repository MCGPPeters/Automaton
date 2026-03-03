namespace Automaton.Tests;

/// <summary>
/// Tests for the <see cref="Result{TSuccess, TError}"/> discriminated union,
/// covering factory methods, projections, monadic operations, catamorphism,
/// Try-pattern extraction, defaults, and implicit conversion.
/// </summary>
public sealed class ResultTests
{
    // ── Factory methods ───────────────────────────────────────────────

    [Fact]
    public void Ok_creates_success_result()
    {
        var result = Result<int, string>.Ok(42);

        Assert.True(result.IsOk);
        Assert.False(result.IsErr);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Err_creates_error_result()
    {
        var result = Result<int, string>.Err("boom");

        Assert.False(result.IsOk);
        Assert.True(result.IsErr);
        Assert.Equal("boom", result.Error);
    }

    // ── Throwing accessors ────────────────────────────────────────────

    [Fact]
    public void Value_on_Err_throws_InvalidOperationException()
    {
        var result = Result<int, string>.Err("nope");

        var ex = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("Err", ex.Message);
    }

    [Fact]
    public void Error_on_Ok_throws_InvalidOperationException()
    {
        var result = Result<int, string>.Ok(1);

        var ex = Assert.Throws<InvalidOperationException>(() => result.Error);
        Assert.Contains("Ok", ex.Message);
    }

    // ── Match (catamorphism) ──────────────────────────────────────────

    [Fact]
    public void Match_invokes_ok_branch_on_success()
    {
        var result = Result<int, string>.Ok(42);

        var message = result.Match(
            ok: v => $"value={v}",
            err: e => $"error={e}");

        Assert.Equal("value=42", message);
    }

    [Fact]
    public void Match_invokes_err_branch_on_failure()
    {
        var result = Result<int, string>.Err("boom");

        var message = result.Match(
            ok: v => $"value={v}",
            err: e => $"error={e}");

        Assert.Equal("error=boom", message);
    }

    [Fact]
    public void Match_supports_type_coercion_through_common_base()
    {
        var ok = Result<int, string>.Ok(1);
        var err = Result<int, string>.Err("x");

        // Both branches return object — verifying generic TOut works
        object okResult = ok.Match<object>(ok: v => v, err: e => e);
        object errResult = err.Match<object>(ok: v => v, err: e => e);

        Assert.Equal(1, okResult);
        Assert.Equal("x", errResult);
    }

    // ── Switch (void catamorphism) ───────────────────────────────────

    [Fact]
    public void Switch_invokes_ok_action_on_success()
    {
        var result = Result<int, string>.Ok(42);
        var captured = 0;

        result.Switch(
            ok: v => captured = v,
            err: _ => captured = -1);

        Assert.Equal(42, captured);
    }

    [Fact]
    public void Switch_invokes_err_action_on_failure()
    {
        var result = Result<int, string>.Err("fail");
        var captured = "";

        result.Switch(
            ok: _ => captured = "ok",
            err: e => captured = e);

        Assert.Equal("fail", captured);
    }

    // ── TryGetValue / TryGetError ────────────────────────────────────

    [Fact]
    public void TryGetValue_returns_true_and_value_on_Ok()
    {
        var result = Result<int, string>.Ok(42);

        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryGetValue_returns_false_on_Err()
    {
        var result = Result<int, string>.Err("nope");

        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public void TryGetError_returns_true_and_error_on_Err()
    {
        var result = Result<int, string>.Err("boom");

        Assert.True(result.TryGetError(out var error));
        Assert.Equal("boom", error);
    }

    [Fact]
    public void TryGetError_returns_false_on_Ok()
    {
        var result = Result<int, string>.Ok(1);

        Assert.False(result.TryGetError(out _));
    }

    // ── DefaultValue / DefaultError ──────────────────────────────────

    [Fact]
    public void DefaultValue_returns_value_on_Ok()
    {
        var result = Result<int, string>.Ok(42);

        Assert.Equal(42, result.DefaultValue(0));
    }

    [Fact]
    public void DefaultValue_returns_fallback_on_Err()
    {
        var result = Result<int, string>.Err("nope");

        Assert.Equal(-1, result.DefaultValue(-1));
    }

    [Fact]
    public void DefaultError_returns_error_on_Err()
    {
        var result = Result<int, string>.Err("boom");

        Assert.Equal("boom", result.DefaultError("fallback"));
    }

    [Fact]
    public void DefaultError_returns_fallback_on_Ok()
    {
        var result = Result<int, string>.Ok(1);

        Assert.Equal("fallback", result.DefaultError("fallback"));
    }

    // ── Implicit conversion ──────────────────────────────────────────

    [Fact]
    public void Implicit_conversion_from_TSuccess_creates_Ok()
    {
        Result<int, string> result = 42;

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Implicit_conversion_works_in_method_return()
    {
        static Result<string, int> GetName() => "Alice";

        var result = GetName();

        Assert.True(result.IsOk);
        Assert.Equal("Alice", result.Value);
    }

    // ── Map / Select (functor) ───────────────────────────────────────

    [Fact]
    public void Map_transforms_Ok_value()
    {
        var result = Result<int, string>.Ok(21).Map(v => v * 2);

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Map_propagates_Err()
    {
        var result = Result<int, string>.Err("fail").Map(v => v * 2);

        Assert.True(result.IsErr);
        Assert.Equal("fail", result.Error);
    }

    [Fact]
    public void Select_is_LINQ_alias_for_Map()
    {
        var result = from v in Result<int, string>.Ok(21) select v * 2;

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    // ── Bind (monad) ─────────────────────────────────────────────────

    [Fact]
    public void Bind_chains_Ok_to_Ok()
    {
        var result = Result<int, string>.Ok(21)
            .Bind(v => Result<int, string>.Ok(v * 2));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Bind_chains_Ok_to_Err()
    {
        var result = Result<int, string>.Ok(21)
            .Bind(_ => Result<int, string>.Err("failed"));

        Assert.True(result.IsErr);
        Assert.Equal("failed", result.Error);
    }

    [Fact]
    public void Bind_short_circuits_on_Err()
    {
        var called = false;
        var result = Result<int, string>.Err("first")
            .Bind(v =>
            {
                called = true;
                return Result<int, string>.Ok(v);
            });

        Assert.True(result.IsErr);
        Assert.Equal("first", result.Error);
        Assert.False(called);
    }

    // ── SelectMany (LINQ multi-from) ─────────────────────────────────

    [Fact]
    public void SelectMany_supports_LINQ_query_syntax()
    {
        static Result<int, string> Parse(string s) =>
            int.TryParse(s, out var n)
                ? Result<int, string>.Ok(n)
                : Result<int, string>.Err($"Cannot parse '{s}'");

        var result =
            from x in Parse("10")
            from y in Parse("32")
            select x + y;

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void SelectMany_short_circuits_on_first_Err()
    {
        var secondCalled = false;

        var result =
            from x in Result<int, string>.Err("first failed")
            from y in Invoke(() => { secondCalled = true; return Result<int, string>.Ok(2); })
            select x + y;

        Assert.True(result.IsErr);
        Assert.Equal("first failed", result.Error);
        Assert.False(secondCalled);
    }

    [Fact]
    public void SelectMany_short_circuits_on_second_Err()
    {
        var result =
            from x in Result<int, string>.Ok(1)
            from y in Result<int, string>.Err("second failed")
            select x + y;

        Assert.True(result.IsErr);
        Assert.Equal("second failed", result.Error);
    }

    // ── MapError ─────────────────────────────────────────────────────

    [Fact]
    public void MapError_transforms_Err_value()
    {
        var result = Result<int, string>.Err("boom")
            .MapError(e => e.Length);

        Assert.True(result.IsErr);
        Assert.Equal(4, result.Error);
    }

    [Fact]
    public void MapError_preserves_Ok()
    {
        var result = Result<int, string>.Ok(42)
            .MapError(e => e.Length);

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    // ── ToString ─────────────────────────────────────────────────────

    [Fact]
    public void ToString_Ok_includes_value() =>
        Assert.Equal("Ok(42)", Result<int, string>.Ok(42).ToString());

    [Fact]
    public void ToString_Err_includes_error() =>
        Assert.Equal("Err(boom)", Result<int, string>.Err("boom").ToString());

    // ── Unit type ────────────────────────────────────────────────────

    [Fact]
    public void Unit_Value_is_default() =>
        Assert.Equal(default, Unit.Value);

    [Fact]
    public void Unit_ToString_returns_parentheses() =>
        Assert.Equal("()", Unit.Value.ToString());

    [Fact]
    public void Result_Unit_represents_void_success()
    {
        var result = Result<Unit, string>.Ok(Unit.Value);

        Assert.True(result.IsOk);
        Assert.Equal(Unit.Value, result.Value);
    }

    // ── Composition: Match + Map + Bind ──────────────────────────────

    [Fact]
    public void Full_pipeline_with_Match_extraction()
    {
        var output = Result<int, string>.Ok(10)
            .Map(v => v * 2)
            .Bind(v => v > 15
                ? Result<string, string>.Ok($"big: {v}")
                : Result<string, string>.Err("too small"))
            .Match(
                ok: v => v,
                err: e => $"error: {e}");

        Assert.Equal("big: 20", output);
    }

    [Fact]
    public void Full_pipeline_error_path()
    {
        var output = Result<int, string>.Ok(5)
            .Map(v => v * 2)
            .Bind(v => v > 15
                ? Result<string, string>.Ok($"big: {v}")
                : Result<string, string>.Err("too small"))
            .Match(
                ok: v => v,
                err: e => $"error: {e}");

        Assert.Equal("error: too small", output);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Helper to wrap a lambda call so we can track whether it was invoked.
    /// </summary>
    private static T Invoke<T>(Func<T> f) => f();
}
