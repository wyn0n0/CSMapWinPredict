namespace CsDemoMap.Api.Services;

internal enum SituationSceneErrorCode
{
    ImportNotFound,
    ImportNotReady,
    UnsupportedMap,
    InvalidWindow,
    InvalidTick,
    FrameUnavailable,
    SidecarMissing,
    VersionMismatch,
    CorruptData,
    DuplicateConflict,
    SourceMismatch,
    ServiceBusy
}

internal sealed class SituationSceneException : Exception
{
    public SituationSceneException(
        SituationSceneErrorCode code,
        string safeMessage,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Code = code;
    }

    public SituationSceneErrorCode Code { get; }
}

internal enum SituationSceneErrorContext
{
    ImportedDemo,
    StoredWindows,
    Timeline,
    WindowObjects,
    SidecarRegistration
}

internal static class SituationSceneErrorMapper
{
    public static SituationSceneException Map(
        Exception exception,
        SituationSceneErrorContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
            throw exception;
        if (exception is SituationSceneException mapped)
            return mapped;

        var code = Classify(exception, context);
        return new SituationSceneException(code, SafeMessage(code), exception);
    }

    private static SituationSceneErrorCode Classify(
        Exception exception,
        SituationSceneErrorContext context)
    {
        if (exception is SituationServiceBusyException or ObjectDisposedException)
            return SituationSceneErrorCode.ServiceBusy;
        if (exception is KeyNotFoundException &&
            context is SituationSceneErrorContext.ImportedDemo or SituationSceneErrorContext.SidecarRegistration)
            return SituationSceneErrorCode.ImportNotFound;
        if (exception is NotSupportedException)
            return SituationSceneErrorCode.UnsupportedMap;
        if (exception is ArgumentOutOfRangeException range)
            return string.Equals(range.ParamName, "requestedTick", StringComparison.Ordinal)
                ? SituationSceneErrorCode.InvalidTick
                : SituationSceneErrorCode.InvalidWindow;
        if (exception is OverflowException)
            return SituationSceneErrorCode.InvalidWindow;

        var message = exception.Message;
        if (Contains(message, "not ready for situation"))
            return SituationSceneErrorCode.ImportNotReady;
        if (Contains(message, "No frame exists"))
            return SituationSceneErrorCode.FrameUnavailable;
        if (Contains(message, "sidecar is missing"))
            return SituationSceneErrorCode.SidecarMissing;
        if (Contains(message, "Unsupported situation sidecar version") ||
            Contains(message, "incomplete or unsupported"))
            return SituationSceneErrorCode.VersionMismatch;
        if (Contains(message, "Conflicting duplicate") ||
            Contains(message, "Conflicting segment") ||
            Contains(message, "Conflicting round") ||
            Contains(message, "Conflicting live") ||
            Contains(message, "Conflicting observed"))
            return SituationSceneErrorCode.DuplicateConflict;
        if (Contains(message, "source changed") ||
            Contains(message, "different import directory") ||
            Contains(message, "changed after validation") ||
            Contains(message, "file hash mismatch") ||
            Contains(message, "map or tick rate does not match") ||
            Contains(message, "bounds do not match") ||
            Contains(message, "indexes do not match") ||
            Contains(message, "source demo"))
            return SituationSceneErrorCode.SourceMismatch;
        if (exception is ArgumentException argument &&
            string.Equals(argument.ParamName, "demoId", StringComparison.Ordinal))
            return SituationSceneErrorCode.ImportNotFound;

        return SituationSceneErrorCode.CorruptData;
    }

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string SafeMessage(SituationSceneErrorCode code) => code switch
    {
        SituationSceneErrorCode.ImportNotFound => "Imported demo was not found.",
        SituationSceneErrorCode.ImportNotReady => "Imported demo is not ready for situation analysis.",
        SituationSceneErrorCode.UnsupportedMap => "This map is not supported for situation analysis.",
        SituationSceneErrorCode.InvalidWindow => "The requested replay window is invalid.",
        SituationSceneErrorCode.InvalidTick => "The requested tick is invalid.",
        SituationSceneErrorCode.FrameUnavailable => "No replay frame is available for the requested tick.",
        SituationSceneErrorCode.SidecarMissing => "Situation data is missing and must be rebuilt explicitly.",
        SituationSceneErrorCode.VersionMismatch => "The situation data version is not supported.",
        SituationSceneErrorCode.CorruptData => "The stored situation data is invalid.",
        SituationSceneErrorCode.DuplicateConflict => "Stored situation observations conflict.",
        SituationSceneErrorCode.SourceMismatch => "The situation data does not match the imported demo.",
        SituationSceneErrorCode.ServiceBusy => "The situation scene service is busy.",
        _ => "Situation scene construction failed."
    };
}

internal sealed record SituationStoredWindowInput(
    SceneInputPrefix Input,
    int WindowsRead);

internal sealed record SituationSceneBuildProduct(
    SituationSceneCacheValue Value,
    int WindowsRead,
    TimeSpan InputElapsed,
    TimeSpan CompletionElapsed);
