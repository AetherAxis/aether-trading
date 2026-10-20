namespace Aether.Quotes.Domain;

/// <summary>
/// Quotes' lookup port for securities — an anti-corruption layer over the
/// Securities context. Quotes depends only on this port (+ <see cref="SecurityRef"/>);
/// the host (or the AI adapter) provides an implementation that adapts the
/// Securities context's <c>ISecurityManager</c>. This keeps Quotes decoupled
/// from the Securities context's entity/repository.
/// </summary>
public interface ISecurityLookup
{
    /// <summary>Searches securities by keyword (code or name); returns Quotes' view of them.</summary>
    Task<List<SecurityRef>> SearchAsync(string keyword);
}
