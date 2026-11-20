namespace FacultyMessenger.Api.Domain.Entities;

/// <summary>Учебная группа (например, "413.2") со старостой и куратором.</summary>
public class AcademicGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public int Course { get; set; }
    public string Faculty { get; set; } = null!;

    /// <summary>Староста группы (выбирается из студентов этой группы).</summary>
    public Guid? LeaderId { get; set; }
    public User? Leader { get; set; }

    /// <summary>Куратор группы (необязательное поле, выбирается из Employee).</summary>
    public Guid? CuratorId { get; set; }
    public User? Curator { get; set; }

    public ICollection<User> Students { get; set; } = new List<User>();
    public ICollection<ChatGroupBinding> ChatBindings { get; set; } = new List<ChatGroupBinding>();
}
