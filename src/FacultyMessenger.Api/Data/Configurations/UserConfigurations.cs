using FacultyMessenger.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FacultyMessenger.Api.Data.Configurations;

public class AcademicGroupConfiguration : IEntityTypeConfiguration<AcademicGroup>
{
    public void Configure(EntityTypeBuilder<AcademicGroup> b)
    {
        b.ToTable("academic_groups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Name).IsRequired().HasMaxLength(32);
        b.Property(x => x.Faculty).IsRequired().HasMaxLength(200);
        b.Property(x => x.Course).IsRequired();

        b.HasIndex(x => x.Name).IsUnique();
        b.HasIndex(x => x.LeaderId);
        b.HasIndex(x => x.CuratorId);

        b.HasOne(x => x.Leader)
            .WithMany()
            .HasForeignKey(x => x.LeaderId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.Curator)
            .WithMany(u => u.CuratedGroups)
            .HasForeignKey(x => x.CuratorId)
            .OnDelete(DeleteBehavior.SetNull);

        b.ToTable(t => t.HasCheckConstraint("ck_academic_groups_course", "\"Course\" BETWEEN 1 AND 6"));
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.FullName).IsRequired().HasMaxLength(200);
        b.Property(x => x.Email).IsRequired().HasMaxLength(254);
        b.Property(x => x.PasswordHash).IsRequired().HasMaxLength(100);
        b.Property(x => x.Role).IsRequired().HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EiosExternalId).HasMaxLength(100);
        b.Property(x => x.DefaultNotificationsEnabled).HasDefaultValue(true);

        b.HasIndex(x => x.Email).IsUnique();
        b.HasIndex(x => x.EiosExternalId).IsUnique().HasFilter("\"EiosExternalId\" IS NOT NULL");
        b.HasIndex(x => new { x.AcademicGroupId, x.Role });

        b.HasOne(x => x.AcademicGroup)
            .WithMany(g => g.Students)
            .HasForeignKey(x => x.AcademicGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
