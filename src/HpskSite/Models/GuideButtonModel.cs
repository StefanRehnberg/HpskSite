namespace HpskSite.Models
{
    /// <summary>Underlag till delen <c>Partials/_GuideButton.cshtml</c>: id i TutorialHelper (tutorialnodens <c>tutorialId</c>), tooltip och storlek.</summary>
    public sealed record GuideButtonModel(string TutorialId, string Title = "Se instruktionsfilm om den här delen", bool Small = false, string? ExtraClass = null);
}
