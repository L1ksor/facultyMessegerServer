using FacultyMessenger.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AcademicGroup> AcademicGroups => Set<AcademicGroup>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatGroupBinding> ChatGroupBindings => Set<ChatGroupBinding>();
    public DbSet<ChatParticipant> ChatParticipants => Set<ChatParticipant>();
    public DbSet<UserChatNotificationSetting> UserChatNotificationSettings => Set<UserChatNotificationSetting>();
    public DbSet<ChatRole> ChatRoles => Set<ChatRole>();
    public DbSet<ChatMemberRole> ChatMemberRoles => Set<ChatMemberRole>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<MessageAcknowledgement> MessageAcknowledgements => Set<MessageAcknowledgement>();
    public DbSet<Poll> Polls => Set<Poll>();
    public DbSet<PollOption> PollOptions => Set<PollOption>();
    public DbSet<PollVote> PollVotes => Set<PollVote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
