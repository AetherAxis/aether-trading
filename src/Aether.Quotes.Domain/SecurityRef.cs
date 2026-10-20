namespace Aether.Quotes.Domain;

/// <summary>
/// Quotes' anti-corruption view of a security (from the Securities context).
/// A plain DTO — Quotes does not reference the Securities context's <c>Security</c>
/// entity, only this local snapshot, so the two contexts stay decoupled.
/// </summary>
public sealed class SecurityRef
{
    public int Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    public string BaseCode { get; init; } = string.Empty;
}
