using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class CreateMatchPage : ContentPage
{
    public CreateMatchPage(CreateMatchViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnHandicapInfoTapped(object sender, EventArgs e)
    {
        var message = @"Handicap gör det möjligt för skyttar på olika nivåer att tävla rättvist mot varandra.

Hur handicap beräknas:
• Baseras på ditt genomsnittliga serieresultat
• En skytt med snitt 48 har handicap 0
• Lägre snitt = positivt handicap (bonus)
• Högre snitt = negativt handicap (avdrag)

Så räknas slutresultatet:
• Handicap läggs till på VARJE serie
• Max 50 poäng per serie (tak)
• Slutresultat = summan av alla justerade serier

Exempel (handicap +3):
Råpoäng: 47, 49, 46
• Serie 1: 47 + 3 = 50 ✓
• Serie 2: 49 + 3 = 52 → 50 (tak)
• Serie 3: 46 + 3 = 49 ✓
• Totalt: 149 poäng

Notera: Höga råpoäng kan ""förlora"" handicap till 50-taket.";

        await DisplayAlert("Så fungerar handicap", message, "OK");
    }
}
