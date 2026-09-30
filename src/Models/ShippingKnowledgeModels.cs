using System.ComponentModel.DataAnnotations;

namespace WebApp.Models;

public sealed record CreateShippingKnowledgeItem
{
    [MaxLength(200)]
    public string? DocumentId { get; init; }

    [Required, MaxLength(20_000)]
    public required string Content { get; init; }

    [Required, MaxLength(200)]
    public required string Source { get; init; }

    [Required, MaxLength(100)]
    public required string Category { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record CreateShippingKnowledgeViewModel
{
    [Display(Name = "Document ID")]
    [MaxLength(200)]
    public string? DocumentId { get; init; }

    [Required, MaxLength(20_000)]
    [DataType(DataType.MultilineText)]
    public string Content { get; init; } = "";

    [Required, MaxLength(200)]
    public string Source { get; init; } = "knowledge-manager";

    [Required, MaxLength(100)]
    public string Category { get; init; } = "shipping";

    [Display(Name = "Tags")]
    public string TagsText { get; init; } = "shipping";
}

public sealed record ShippingKnowledgeItem(
    string Id,
    string DocumentId,
    string Content,
    string Source,
    string Category,
    IReadOnlyList<string> Tags,
    int ChunkIndex,
    IReadOnlyList<float> Embedding,
    DateTimeOffset? CreatedAt,
    int? SchemaVersion);

public sealed record ShippingKnowledgePage(
    IReadOnlyList<ShippingKnowledgeItem> Items,
    string? ContinuationToken);
