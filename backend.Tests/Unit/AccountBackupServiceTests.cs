using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;
using MoneyTracker.Models;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class AccountBackupServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public AccountBackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mt-backup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private AccountBackupService MakeService(int? maxPerUser = null)
    {
        var settings = new Dictionary<string, string?> { ["Backups:AccountBackupPath"] = "backups" };
        if (maxPerUser.HasValue) settings["Backups:MaxAccountBackupsPerUser"] = maxPerUser.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_tempRoot);

        return new AccountBackupService(config, env.Object);
    }

    private static Account MakeAccount(int id = 1, string userId = "user-1", string name = "Checking") => new()
    {
        Id = id,
        UserId = userId,
        Name = name,
        Type = AccountType.Checking,
        OpeningBalance = 100m,
        IsActive = true,
        CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task CreateBackup_WritesFileWithinUserDirectory()
    {
        var svc = MakeService();
        var account = MakeAccount();

        var fileName = await svc.CreateBackupAsync(account, [], note: null);

        var expectedPath = Path.Combine(_tempRoot, "backups", "user-1", fileName);
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateBackup_DefaultNote_MentionsAccountName()
    {
        var svc = MakeService();
        var account = MakeAccount(name: "My Savings");

        var fileName = await svc.CreateBackupAsync(account, [], note: null);
        var json = await File.ReadAllTextAsync(Path.Combine(_tempRoot, "backups", "user-1", fileName));

        json.Should().Contain("My Savings");
        json.Should().Contain("Automatic backup");
    }

    [Fact]
    public async Task CreateBackup_CustomNote_IsPreserved()
    {
        var svc = MakeService();
        var account = MakeAccount();

        var fileName = await svc.CreateBackupAsync(account, [], note: "Closing this account for good.");
        var json = await File.ReadAllTextAsync(Path.Combine(_tempRoot, "backups", "user-1", fileName));

        json.Should().Contain("Closing this account for good.");
    }

    [Fact]
    public async Task CreateBackup_PrunesOldestBeyondMax()
    {
        var svc = MakeService(maxPerUser: 3);
        var account = MakeAccount();

        for (int i = 0; i < 5; i++)
        {
            await svc.CreateBackupAsync(account, [], note: $"backup {i}");
            await Task.Delay(1100); // filenames are second-resolution timestamps — ensure distinct ordering
        }

        var files = Directory.GetFiles(Path.Combine(_tempRoot, "backups", "user-1"));
        files.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListBackups_ReturnsNewestFirstWithSummaryFields()
    {
        var svc = MakeService();
        var account = MakeAccount(name: "Checking");
        await svc.CreateBackupAsync(account, [], note: "first");

        var list = svc.ListBackups("user-1");

        list.Should().ContainSingle();
        list[0].AccountName.Should().Be("Checking");
        list[0].Note.Should().Be("first");
    }

    [Fact]
    public void ListBackups_DifferentUser_SeesNothing()
    {
        var svc = MakeService();
        svc.ListBackups("some-other-user").Should().BeEmpty();
    }

    [Fact]
    public async Task ReadBackup_WrongUser_ReturnsNull()
    {
        var svc = MakeService();
        var fileName = await svc.CreateBackupAsync(MakeAccount(userId: "user-1"), [], note: null);

        svc.ReadBackup("user-2", fileName).Should().BeNull();
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("..\\evil.json")]
    [InlineData("sub/evil.json")]
    [InlineData("sub\\evil.json")]
    public void ReadBackup_PathTraversalAttempt_ReturnsNull(string maliciousName)
    {
        var svc = MakeService();
        svc.ReadBackup("user-1", maliciousName).Should().BeNull();
    }

    [Fact]
    public async Task ReadBackup_ValidFile_ReturnsBytes()
    {
        var svc = MakeService();
        var fileName = await svc.CreateBackupAsync(MakeAccount(), [], note: null);

        var bytes = svc.ReadBackup("user-1", fileName);

        bytes.Should().NotBeNull();
        bytes!.Length.Should().BeGreaterThan(0);
    }
}
