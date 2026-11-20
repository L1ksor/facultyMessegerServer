using FacultyMessenger.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FacultyMessenger.Api.Data.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Text).HasMaxLength(8000);
        b.Property(x => x.MessageType).IsRequired().HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => new { x.ChatId, x.CreatedAt });
        b.HasIndex(x => new { x.ChatId, x.IsPinned }).HasFilter("\"IsPinned\" = TRUE");

        b.HasOne(x => x.Chat)
            .WithMany(c => c.Messages)
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Sender)
            .WithMany(u => u.SentMessages)
            .HasForeignKey(x => x.SenderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> b)
    {
        b.ToTable("attachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(255);
        b.Property(x => x.StoredFilePath).IsRequired().HasMaxLength(500);
        b.Property(x => x.ContentType).IsRequired().HasMaxLength(150);
        b.Property(x => x.FileSize).IsRequired();

        b.HasIndex(x => x.MessageId);
        b.HasIndex(x => x.StoredFilePath).IsUnique();

        b.HasOne(x => x.Message)
            .WithMany(m => m.Attachments)
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_attachments_size", "\"FileSize\" >= 0");
            t.HasCheckConstraint("ck_attachments_duration", "\"DurationSeconds\" IS NULL OR \"DurationSeconds\" >= 0");
        });
    }
}

public class MessageAcknowledgementConfiguration : IEntityTypeConfiguration<MessageAcknowledgement>
{
    public void Configure(EntityTypeBuilder<MessageAcknowledgement> b)
    {
        b.ToTable("message_acknowledgements");
        b.HasKey(x => new { x.MessageId, x.UserId });
        b.Property(x => x.ConfirmedAt).IsRequired();

        b.HasOne(x => x.Message)
            .WithMany(m => m.Acknowledgements)
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.User)
            .WithMany(u => u.Acknowledgements)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.UserId);
    }
}
