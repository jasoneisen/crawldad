using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The per-run secret registry (<see cref="AmbientRunSecretScope"/>, §12, WP3): lifetime and isolation. A registered
/// secret is visible only between <see cref="IRunSecretScope.Begin"/> and disposal, flows down the run's async call
/// chain, and leaves no residue afterwards.
/// </summary>
public class RunSecretScopeTests
{
    [Fact]
    public void Current_is_empty_when_no_scope_is_open()
    {
        var scope = new AmbientRunSecretScope();

        scope.Current.ShouldBeEmpty();
    }

    [Fact]
    public void Register_outside_a_scope_is_a_no_op()
    {
        var scope = new AmbientRunSecretScope();

        scope.Register("a-secret-value"); // no scope open → silently dropped, never stored globally

        scope.Current.ShouldBeEmpty();
    }

    [Fact]
    public void An_open_scope_with_no_secrets_reports_empty()
    {
        var scope = new AmbientRunSecretScope();

        using var _ = scope.Begin();

        scope.Current.ShouldBeEmpty();
    }

    [Fact]
    public void Registered_secrets_are_visible_within_the_scope()
    {
        var scope = new AmbientRunSecretScope();

        using (scope.Begin())
        {
            scope.Register("secret-one");
            scope.Register("secret-two");
            scope.Register("secret-one"); // dedup

            scope.Current.ShouldBe(["secret-one", "secret-two"], ignoreOrder: true);
        }
    }

    [Fact]
    public void Disposing_the_scope_clears_the_secrets()
    {
        var scope = new AmbientRunSecretScope();

        using (scope.Begin())
        {
            scope.Register("secret-value");
        }

        scope.Current.ShouldBeEmpty(); // nothing outlives the run
    }

    [Fact]
    public async Task A_secret_registered_deep_in_the_async_chain_is_visible_at_the_scope_root()
    {
        var scope = new AmbientRunSecretScope();

        using (scope.Begin())
        {
            await RegisterDeepAsync(scope, "async-flowed-secret");

            scope.Current.ShouldContain("async-flowed-secret");
        }
    }

    private static async Task RegisterDeepAsync(AmbientRunSecretScope scope, string secret)
    {
        await Task.Yield(); // cross an await boundary — the ambient scope must still flow to here
        scope.Register(secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Register_rejects_a_missing_secret(string? secret)
    {
        var scope = new AmbientRunSecretScope();
        using var _ = scope.Begin();

        Should.Throw<ArgumentException>(() => scope.Register(secret!));
    }
}
