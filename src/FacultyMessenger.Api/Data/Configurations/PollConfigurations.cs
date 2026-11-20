using FacultyMessenger.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FacultyMessenger.Api.Data.Configurations;

public class PollConfiguration : IEntityTypeConfiguration<Poll>
{
    public void Configure(EntityTypeBuilder<Poll> b)
    {
        b.ToTable("polls");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Question).IsRequired().HasMaxLength(500);

        // Сообщение типа Poll содержит ровно один опрос (1:1)
        b.HasOne(x => x.Message)
            .WithOne(m => m.Poll)
            .HasForeignKey<Poll>(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.MessageId).IsUnique();
    }
}

public class PollOptionConfiguration : IEntityTypeConfiguration<PollOption>
{
    public void Configure(EntityTypeBuilder<PollOption> b)
    {
        b.ToTable("poll_options");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.OptionText).IsRequired().HasMaxLength(300);

        // Оптимистичная блокировка через системный столбец PostgreSQL xmin
        b.Property(x => x.Version).IsRowVersion();

        b.HasOne(x => x.Poll)
            .WithMany(p => p.Options)
            .HasForeignKey(x => x.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.PollId, x.SortOrder });

        // Альтернативный ключ (PollId, Id) — нужен для составного FK из PollVote,
        // который гарантирует на уровне БД, что вариант принадлежит именно этому опросу.
        b.HasAlternateKey(x => new { x.PollId, x.Id });

        b.ToTable(t => t.HasCheckConstraint("ck_poll_options_max_votes", "\"MaxVotes\" IS NULL OR \"MaxVotes\" > 0"));
    }
}

public class PollVoteConfiguration : IEntityTypeConfiguration<PollVote>
{
    public void Configure(EntityTypeBuilder<PollVote> b)
    {
        b.ToTable("poll_votes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.VotedAt).IsRequired();

        // Нельзя дважды проголосовать за один и тот же вариант
        b.HasIndex(x => new { x.PollOptionId, x.UserId }).IsUnique();
        b.HasIndex(x => new { x.PollId, x.UserId });

        b.HasOne(x => x.PollOption)
            .WithMany(o => o.Votes)
            .HasForeignKey(x => new { x.PollId, x.PollOptionId })
            .HasPrincipalKey(o => new { o.PollId, o.Id })
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Poll)
            .WithMany()
            .HasForeignKey(x => x.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.User)
            .WithMany(u => u.PollVotes)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
