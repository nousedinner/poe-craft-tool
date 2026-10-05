using System.Text.Json;

namespace ShiKe.Services;

public enum StorageFailureKind { InvalidJson, InvalidData, FileAccess }

/// <summary>可恢复的文件错误，保留操作、路径和分类；不得把读取失败当成缺少文件。</summary>
public sealed class StorageException : IOException
{
    public string Operation { get; }
    public string FilePath { get; }
    public StorageFailureKind Kind { get; }

    public StorageException(string operation, string filePath, Exception cause)
        : base($"{operation}失败：{Path.GetFileName(filePath)}\n\n文件：{filePath}\n原因：{cause.Message}\n\n" +
               "未自动重置文件。请检查文件内容、权限或占用情况后重试。", cause)
    {
        Operation = operation;
        FilePath = filePath;
        Kind = cause switch
        {
            JsonException => StorageFailureKind.InvalidJson,
            InvalidDataException => StorageFailureKind.InvalidData,
            _ => StorageFailureKind.FileAccess,
        };
        Diag.Log($"[存储] {operation}失败: path={filePath}, kind={Kind}, {cause.GetType().Name}: {cause.Message}");
    }
}
