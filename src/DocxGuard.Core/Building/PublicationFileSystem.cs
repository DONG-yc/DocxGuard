namespace DocxGuard.Core.Building;

internal interface IPublicationFileSystem
{
    Stream CreateNew(string path);
    void Flush(Stream stream);
    void MoveDirectory(string source, string destination);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

internal sealed class PublicationFileSystem : IPublicationFileSystem
{
    public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 8192, FileOptions.SequentialScan);

    public void Flush(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
}
