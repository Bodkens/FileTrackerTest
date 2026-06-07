namespace FileTracker;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

public record FileVersion(string Hash, int Number);

public sealed class FileTrackerService : IDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FileVersion>> memory;
    private readonly string memoryLocation;
    private readonly ILogger<FileTrackerService> logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> folderSemaphores = new();
    private readonly SemaphoreSlim fileSemaphore = new(initialCount: 1, maxCount: 1);

    public async Task<(IEnumerable<string> addedFiles, IEnumerable<string> changedFiles, IEnumerable<string> deletedFiles)> UpdateMemory(string folder, CancellationToken cancellationToken)
    {
        folder = Path.GetFullPath(folder);

        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Specified directory not found");

        bool folderSemaphoreAcquired = false;
        bool fileSemaphoreAcquired = false;
        var semaphore = folderSemaphores.GetOrAdd(folder, _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        var tempFileLocation = Path.Combine(Path.GetDirectoryName(memoryLocation)!, Path.GetRandomFileName());

        try
        {
            await semaphore.WaitAsync(cancellationToken);
            folderSemaphoreAcquired = true;
            var folderMemory = memory.GetOrAdd(folder, _ => new());


            using SHA256 sha256 = SHA256.Create();
            ConcurrentDictionary<string, FileVersion> updatedMemory = [];

            HashSet<string> directories = [..Directory.GetDirectories(folder)];
            HashSet<string> currentFiles = [..Directory.GetFiles(folder), ..directories];
            HashSet<string> relativeCurrentFiles = [..currentFiles.Select(f => Path.GetRelativePath(folder, f))];

            HashSet<string> addedFiles = [];
            HashSet<string> changedFiles = [];

            async Task<string?> GetFileHash(string filePath)
            {
                if (directories.Contains(filePath)) return filePath;

                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var checksum = await sha256.ComputeHashAsync(fs, cancellationToken);
                    return BitConverter.ToString(checksum);
                }
                catch (IOException ex)
                {
                    logger.LogWarning("Could not read file {File}: {Message}", filePath, ex.Message);
                    return null;
                }
            }

            foreach (var currentFileName in currentFiles)
            {
                var currentHash = await GetFileHash(currentFileName);
                if (currentHash is null) continue;
                var relativeFileName = Path.GetRelativePath(folder, currentFileName);

                if (!folderMemory.TryGetValue(relativeFileName, out var version))
                {
                    updatedMemory[relativeFileName] = new FileVersion(currentHash, 1);
                    addedFiles.Add(relativeFileName);
                }
                else
                {
                    bool fileChanged = version.Hash != currentHash;
                    updatedMemory[relativeFileName] = new FileVersion(currentHash, version.Number + Convert.ToInt32(fileChanged));
                    if (fileChanged) changedFiles.Add(relativeFileName);
                }
            }

            HashSet<string> deletedFiles = [..folderMemory.Keys.Where(x => !relativeCurrentFiles.Contains(x))];

            await fileSemaphore.WaitAsync(cancellationToken);
            fileSemaphoreAcquired = true;

            var memorySnapshot = new ConcurrentDictionary<string, ConcurrentDictionary<string, FileVersion>>(memory) { [folder] = updatedMemory };

            using (var fs = new FileStream(tempFileLocation, FileMode.Create))
            {
                await JsonSerializer.SerializeAsync(fs, memorySnapshot, JsonSerializerOptions.Web, cancellationToken);
                await fs.FlushAsync(cancellationToken);
            }

            File.Move(tempFileLocation, memoryLocation, overwrite: true);
            memory[folder] = updatedMemory;

            return (addedFiles, changedFiles, deletedFiles);
        }
        catch (OperationCanceledException cancelled)
        {
            logger.LogInformation("Updating memory cancelled: {Message}", cancelled.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Error updating memory: {Message}", ex.Message);
            throw;
        }
        finally
        {
            if (fileSemaphoreAcquired) fileSemaphore.Release();
            if (folderSemaphoreAcquired) semaphore.Release();
            if (File.Exists(tempFileLocation)) File.Delete(tempFileLocation);
        }
    }

    public IDictionary<string, int> GetFileVersionNumbers(string folder)
    {
        folder = Path.GetFullPath(folder);
        return memory[folder].ToDictionary(x => x.Key, v => v.Value.Number);
    }

    public void Dispose()
    {
        foreach (var (_, semaphore) in folderSemaphores) semaphore.Dispose();
        fileSemaphore.Dispose();
    }

    public FileTrackerService(IConfiguration config, ILogger<FileTrackerService> logger)
    {
        try
        {
            this.logger = logger;

            memoryLocation = Path.GetFullPath(config["FileTracker:MemoryFile"] ?? throw new InvalidOperationException("FileTracker:MemoryFile is not configured"));

            if (!File.Exists(memoryLocation))
            {
                using var fs = File.Create(memoryLocation);
                using var sw = new StreamWriter(fs);
                sw.Write(JsonSerializer.Serialize(new ConcurrentDictionary<string, ConcurrentDictionary<string, FileVersion>>(), JsonSerializerOptions.Web));
                sw.Flush();
            }

            using (var fs = new FileStream(memoryLocation, FileMode.Open))
            {
                try
                {
                    memory = JsonSerializer.Deserialize<ConcurrentDictionary<string, ConcurrentDictionary<string, FileVersion>>>(fs, JsonSerializerOptions.Web)
                             ?? new ();
                }
                catch
                {
                    memory = new ();
                }
            }

            if (this.logger.IsEnabled(LogLevel.Information))
                this.logger.LogInformation("FileTrackerService initialized");
        }
        catch (Exception ex)
        {
            logger.LogCritical("Failed to initialize tracker: {Message}", ex.Message);
            throw;
        }
    }
}
