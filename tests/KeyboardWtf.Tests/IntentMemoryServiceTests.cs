namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class IntentMemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-intent-memory-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitMemoryCanBeUpdatedReadBackAndDeletedDurably()
    {
        var path = Path.Combine(_root, "intent-memory.json");
        var memory = new IntentMemoryService(path);
        memory.Load();

        var created = memory.Remember("preferred editor", "Notepad");
        Assert.Equal("Notepad", memory.ReadPersisted(created.Id)?.Value);
        var updated = memory.Remember("preferred editor", "Visual Studio Code");
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(
            "Visual Studio Code",
            memory.ReadPersisted("preferred editor")?.Value);

        var freshConversation = new IntentMemoryService(path);
        freshConversation.Load();
        Assert.Equal(
            "Visual Studio Code",
            freshConversation.Search("preferred editor").Single().Value);
        Assert.True(freshConversation.Forget("preferred editor"));
        Assert.Null(freshConversation.ReadPersisted("preferred editor"));
    }

    [Fact]
    public void Failed_publish_cleans_staging_without_replacing_existing_saved_memory()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "intent-memory.json");
        var memory = new IntentMemoryService(path);
        memory.Load();
        memory.Remember("preferred editor", "Notepad");
        var original = File.ReadAllBytes(path);
        using (var pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            // Windows may surface the blocked overwrite as sharing violation
            // (IOException) or access denied (UnauthorizedAccessException).
            Assert.True(Record.Exception(() => memory.Remember("preferred editor", "Other editor"))
                is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Failed_publish_never_removes_a_directory_at_the_destination()
    {
        var path = Path.Combine(_root, "intent-memory.json");
        Directory.CreateDirectory(path);
        var sentinel = Path.Combine(path, "keep.txt");
        File.WriteAllText(sentinel, "untouched");
        var memory = new IntentMemoryService(path);
        Assert.True(Record.Exception(() => memory.Remember("preferred editor", "Notepad"))
            is IOException or UnauthorizedAccessException);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch { }
    }
}
