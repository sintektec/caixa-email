using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Persistence;
using Xunit;

namespace Sintek.Mail.PersistenceTests;

public sealed class MailDbContextTests : IDisposable
{
    private readonly MailDbContext _context;

    public MailDbContextTests()
    {
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new MailDbContext(options);
    }

    [Fact]
    public async Task CanAddAndRetrieveDomainDirectory()
    {
        var domain = new DomainDirectory("example.com", "Test");

        _context.Domains.Add(domain);
        await _context.SaveChangesAsync();

        var retrieved = await _context.Domains.FirstOrDefaultAsync(d => d.DomainName == "example.com");
        Assert.NotNull(retrieved);
        Assert.Equal("Test", retrieved.Description);
    }

    [Fact]
    public async Task CanAddAndRetrieveAccount()
    {
        var domain = new DomainDirectory("example.com");
        _context.Domains.Add(domain);

        var account = new Account
        {
            DomainId = domain.Id,
            EmailAddress = "user@example.com",
            DisplayName = "User",
            ImapHost = "imap.example.com",
            ImapPort = 993,
            SmtpHost = "smtp.example.com",
            SmtpPort = 587
        };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        var retrieved = await _context.Accounts.FirstOrDefaultAsync(a => a.EmailAddress == "user@example.com");
        Assert.NotNull(retrieved);
        Assert.Equal("User", retrieved.DisplayName);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
