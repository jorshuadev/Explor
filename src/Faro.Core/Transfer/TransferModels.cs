namespace Faro.Core.Transfer;

public enum TransferKind { Copy, Move, Duplicate }

public enum TransferState
{
    Preparing, Running, Paused, WaitingForDevice, WaitingForUser, Finishing, Completed, Canceled, Failed
}

public enum ConflictChoice { Stop, Skip, KeepBoth, Replace, Merge }

public enum ErrorChoice { Retry, Skip, SkipAll, Stop }

public sealed class ConflictRequest
{
    public string SourcePath { get; init; }
    public string DestinationPath { get; init; }
    public bool SourceIsDirectory { get; init; }
    public bool DestinationIsDirectory { get; init; }
    public long SourceSize { get; init; }
    public long DestinationSize { get; init; }
    public DateTime SourceModified { get; init; }
    public DateTime DestinationModified { get; init; }
    public bool IsTopLevel { get; init; }
    public bool CanMerge => SourceIsDirectory && DestinationIsDirectory;
    public string Name => Path.GetFileName(DestinationPath);
}

public sealed class ConflictAnswer
{
    public ConflictChoice Choice { get; init; }
    public bool ApplyToAll { get; init; }
    public ConflictAnswer(ConflictChoice c, bool all = false) { Choice = c; ApplyToAll = all; }
}

public sealed class ErrorRequest
{
    public string Path { get; init; }
    public string Message { get; init; }
    public Exception Exception { get; init; }
}

/// <summary>Lo que la interfaz debe contestar cuando el motor necesita una decisión.</summary>
public interface ITransferPrompt
{
    Task<ConflictAnswer> ResolveConflictAsync(TransferJob job, ConflictRequest request);
    Task<ErrorChoice> ResolveErrorAsync(TransferJob job, ErrorRequest request);
}

/// <summary>Respuestas automáticas (pruebas y modo sin interfaz).</summary>
public sealed class AutoPrompt : ITransferPrompt
{
    public ConflictChoice Conflict { get; set; } = ConflictChoice.Replace;
    public ErrorChoice Error { get; set; } = ErrorChoice.Skip;
    public Task<ConflictAnswer> ResolveConflictAsync(TransferJob job, ConflictRequest r)
    {
        var c = Conflict == ConflictChoice.Merge && !r.CanMerge ? ConflictChoice.Replace : Conflict;
        return Task.FromResult(new ConflictAnswer(c, true));
    }
    public Task<ErrorChoice> ResolveErrorAsync(TransferJob job, ErrorRequest r) => Task.FromResult(Error);
}

public sealed record TransferIssue(string Path, string Message);
