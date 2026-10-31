using System.Text.Json;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Auth;

/// <summary>
/// OAuth client configuration. Values come from <c>&lt;data root&gt;\auth-config.json</c>
/// (<c>{"microsoftClientId": "...", "googleClientId": "...", "googleClientSecret": "..."}</c>); the environment
/// variables <c>MAILWARDEN_MS_CLIENT_ID</c>, <c>MAILWARDEN_GOOGLE_CLIENT_ID</c> and
/// <c>MAILWARDEN_GOOGLE_CLIENT_SECRET</c> override them. Missing values are only an error when the provider is used
/// (<see cref="RequireMicrosoftClientId"/>, <see cref="RequireGoogleClient"/>). <see cref="ToString"/> never shows
/// the values.
/// </summary>
/// <param name="MicrosoftClientId">Application (client) id of the Microsoft app registration, or null.</param>
/// <param name="GoogleClientId">Client id of the Google desktop OAuth client, or null.</param>
/// <param name="GoogleClientSecret">Client secret of the Google desktop OAuth client, or null.</param>
public sealed record AuthConfiguration(string? MicrosoftClientId, string? GoogleClientId, string? GoogleClientSecret)
{
    // Environment variables (override) and JSON property names (file) per value.
    private const string MicrosoftClientIdVariable = "MAILWARDEN_MS_CLIENT_ID";
    private const string GoogleClientIdVariable = "MAILWARDEN_GOOGLE_CLIENT_ID";
    private const string GoogleClientSecretVariable = "MAILWARDEN_GOOGLE_CLIENT_SECRET";
    private const string MicrosoftClientIdProperty = "microsoftClientId";
    private const string GoogleClientIdProperty = "googleClientId";
    private const string GoogleClientSecretProperty = "googleClientSecret";

    /// <summary>
    /// Loads the configuration: file values first, then non-empty environment variables override them. Empty or
    /// whitespace values count as missing. A missing file is allowed.
    /// </summary>
    /// <param name="authConfigPath">Path of <c>auth-config.json</c>.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable (null when unset).</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="AuthenticationFailedException">
    /// The file is not valid JSON, not a JSON object, or a known property is not a string
    /// (<see cref="AuthFailureReason.Configuration"/>; <see cref="AuthenticationFailedException.Provider"/> is
    /// <see cref="MailProvider.GenericImap"/> because the file is shared by all providers).
    /// </exception>
    public static AuthConfiguration Load(string authConfigPath, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authConfigPath);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        // File values (optional file).
        AuthConfiguration fileValues = File.Exists(authConfigPath)
            ? ParseFile(authConfigPath)
            : new AuthConfiguration(null, null, null);

        // Environment overrides.
        return new AuthConfiguration(
            Normalise(getEnvironmentVariable(MicrosoftClientIdVariable)) ?? fileValues.MicrosoftClientId,
            Normalise(getEnvironmentVariable(GoogleClientIdVariable)) ?? fileValues.GoogleClientId,
            Normalise(getEnvironmentVariable(GoogleClientSecretVariable)) ?? fileValues.GoogleClientSecret);
    }

    /// <summary>Returns the Microsoft client id.</summary>
    /// <returns>The client id.</returns>
    /// <exception cref="AuthenticationFailedException">
    /// The value is missing (<see cref="AuthFailureReason.Configuration"/>; the message names the missing key).
    /// </exception>
    public string RequireMicrosoftClientId()
    {
        return MicrosoftClientId ?? throw MissingValues(
            MailProvider.Microsoft,
            [$"{MicrosoftClientIdVariable} (or {MicrosoftClientIdProperty})"]);
    }

    /// <summary>Returns the Google client id and secret.</summary>
    /// <returns>Client id and client secret.</returns>
    /// <exception cref="AuthenticationFailedException">
    /// A value is missing (<see cref="AuthFailureReason.Configuration"/>; the message names the missing keys).
    /// </exception>
    public (string ClientId, string ClientSecret) RequireGoogleClient()
    {
        var missingKeys = new List<string>();
        if (GoogleClientId is null)
        {
            missingKeys.Add($"{GoogleClientIdVariable} (or {GoogleClientIdProperty})");
        }

        if (GoogleClientSecret is null)
        {
            missingKeys.Add($"{GoogleClientSecretVariable} (or {GoogleClientSecretProperty})");
        }

        if (missingKeys.Count > 0)
        {
            throw MissingValues(MailProvider.Google, missingKeys);
        }

        return (GoogleClientId!, GoogleClientSecret!);
    }

    /// <summary>Shows which values are set, never the values themselves.</summary>
    /// <returns>Presence of each value.</returns>
    public override string ToString()
    {
        return $"AuthConfiguration {{ MicrosoftClientId = {Presence(MicrosoftClientId)}, "
            + $"GoogleClientId = {Presence(GoogleClientId)}, GoogleClientSecret = {Presence(GoogleClientSecret)} }}";
    }

    private static AuthConfiguration ParseFile(string authConfigPath)
    {
        string fileName = Path.GetFileName(authConfigPath);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authConfigPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidFile($"The sign-in configuration file {fileName} must contain a JSON object.");
            }

            return new AuthConfiguration(
                ReadString(root, MicrosoftClientIdProperty, fileName),
                ReadString(root, GoogleClientIdProperty, fileName),
                ReadString(root, GoogleClientSecretProperty, fileName));
        }
        catch (JsonException exception)
        {
            // Position only: the parser message may quote parts of the file content (secrets).
            throw InvalidFile(
                $"The sign-in configuration file {fileName} is not valid JSON "
                + $"(line {exception.LineNumber + 1}, byte {exception.BytePositionInLine + 1}).");
        }
    }

    private static string? ReadString(JsonElement root, string propertyName, string fileName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidFile($"The value of {propertyName} in {fileName} must be a string.");
        }

        return Normalise(value.GetString());
    }

    private static string? Normalise(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Presence(string? value)
    {
        return value is null ? "missing" : "set";
    }

    private static AuthenticationFailedException InvalidFile(string message)
    {
        return new AuthenticationFailedException(MailProvider.GenericImap, AuthFailureReason.Configuration, message);
    }

    private static AuthenticationFailedException MissingValues(MailProvider provider, IReadOnlyList<string> missingKeys)
    {
        return new AuthenticationFailedException(
            provider,
            AuthFailureReason.Configuration,
            $"{provider} sign-in is not configured: {string.Join(" and ", missingKeys)} "
            + $"{(missingKeys.Count == 1 ? "is" : "are")} missing in the environment or in auth-config.json.");
    }
}
