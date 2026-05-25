using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    [TableName("FieldTarget")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FieldTarget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Number of individual targets on this figure. Default 1.</summary>
        public int TargetsPerFigure { get; set; } = 1;
        /// <summary>Size group 1-15. 15 means "Ej grupperad". Used to derive the max distance bucket for automatic shoot-time suggestions per SHB.</summary>
        public int SizeGroup { get; set; } = 15;
    }

    [TableName("FieldTargetVariant")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FieldTargetVariant
    {
        public int Id { get; set; }
        public int TargetId { get; set; }
        public string FullName { get; set; } = "";
        public string ImageName { get; set; } = "";
        public string Color { get; set; } = "";
    }

    // View model for API response
    public class FieldTargetView
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int TargetsPerFigure { get; set; } = 1;
        public int SizeGroup { get; set; } = 15;
        public List<FieldTargetVariantView> Variants { get; set; } = new();
    }

    public class FieldTargetVariantView
    {
        public int Id { get; set; }
        public string FullName { get; set; } = "";
        public string ImageName { get; set; } = "";
        public string ImageUrl => $"/images/field-targets/{ImageName}";
        public string Color { get; set; } = "";
    }

    public class UpdateTargetRequest
    {
        public int TargetId { get; set; }
        public string? Name { get; set; }
        public int? TargetsPerFigure { get; set; }
        public int? SizeGroup { get; set; }
        public List<UpdateVariantRequest>? Variants { get; set; }
    }

    public class UpdateVariantRequest
    {
        public int Id { get; set; }
        public string? FullName { get; set; }
        public string? Color { get; set; }
    }

    public class CreateTargetRequest
    {
        public string Name { get; set; } = "";
        public int TargetsPerFigure { get; set; } = 1;
        public int SizeGroup { get; set; } = 15;
        public List<CreateVariantRequest>? Variants { get; set; }
    }

    public class CreateVariantRequest
    {
        public string FullName { get; set; } = "";
        public string ImageName { get; set; } = "";
        public string Color { get; set; } = "";
    }

    public class DeleteTargetRequest
    {
        public int TargetId { get; set; }
    }

    public class AddVariantRequest
    {
        public int TargetId { get; set; }
        public string FullName { get; set; } = "";
        public string ImageName { get; set; } = "";
        public string Color { get; set; } = "";
    }

    public class DeleteVariantRequest
    {
        public int VariantId { get; set; }
    }

    public class MoveVariantRequest
    {
        public int VariantId { get; set; }
        public int NewTargetId { get; set; }
    }
}
