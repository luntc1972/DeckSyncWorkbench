using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>
/// Outcome of a Deck Modules service operation: either a value on success, or a user-facing
/// validation/upstream error message on failure. Never throws for expected validation failures.
/// </summary>
/// <typeparam name="T">The type of value produced on success.</typeparam>
public sealed record DeckModulesServiceResult<T>
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets the user-facing error message when <see cref="Succeeded"/> is <see langword="false"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the produced value when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    public T? Value { get; init; }

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    public static DeckModulesServiceResult<T> Success(T value) => new() { Succeeded = true, Value = value };

    /// <summary>Creates a failed result carrying <paramref name="errorMessage"/>.</summary>
    public static DeckModulesServiceResult<T> Failure(string errorMessage) => new() { Succeeded = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Turns one imported baseline deck into a browser-session Deck Modules project and compiles the
/// current manual state through the Phase 1 compiler. Holds no server-side project state between
/// calls: every call is scoped to the request it receives.
/// </summary>
public interface IDeckModulesPageService
{
    /// <summary>
    /// Imports exactly one baseline deck from a public URL or pasted decklist text and returns its
    /// immutable command zone plus baseline mainboard entries for manual module assignment.
    /// </summary>
    /// <param name="request">The import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeckModulesServiceResult<DeckModulesViewModel>> ImportAsync(
        DeckModulesImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compiles the submitted browser-session project and active selection. Performs no outbound
    /// network calls: card-legality facts are either already injected or reported as unverifiable.
    /// </summary>
    /// <param name="request">The compilation request.</param>
    DeckModulesServiceResult<DeckModulesCompilationViewModel> Compile(DeckModulesCompilationRequest request);
}
