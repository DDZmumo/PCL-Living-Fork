using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PCLLF.Presentation.Foundation;

// LF Controls/ModernTooltipTheme.vb at main50, LF LICENCE. No tooltip replacement:
// the pinned renderer owns borrowing, placement, animations and the actual Border.
public sealed class LegacyTooltipThemeAdapter {
    private readonly Func<Border?> currentCard;
    private bool installed;
    private Border? trackedCard;
    private TextBlock? trackedText;
    public LegacyTooltipThemeAdapter(Func<Border?> currentCard) => this.currentCard = currentCard;
#if !NETFRAMEWORK
    // NativeWpf is source-compiled into modern Presentation; its card stays internal.
    public static LegacyTooltipThemeAdapter ForNativeTooltip() {
        var adapter = new LegacyTooltipThemeAdapter(() => MeloongCore.Wpf.ModernTooltipService.ThemeCard);
        MeloongCore.Wpf.ModernTooltipService.ThemeCardChanged += adapter.NativeCardChanged;
        return adapter;
    }
#endif
    public void Install() {
        if (!installed) {
            installed = true;
            EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent, new RoutedEventHandler(CardLoaded));
        }
        ApplyCard(currentCard());
    }
    private void NativeCardChanged(Border card) {
        if (installed) ApplyCard(card);
    }
    private void ApplyCard(Border? card) {
        if (card is null) return;
        card.SetResourceReference(Border.BackgroundProperty, "ColorBrushPopup");
        card.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5");
        card.SetResourceReference(TextElement.ForegroundProperty, "ColorBrush1");
        if (card.Child is TextBlock text) text.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        if (!ReferenceEquals(trackedCard, card)) {
            if (trackedCard is not null) {
                trackedCard.LayoutUpdated -= ContentLayoutUpdated;
                trackedCard.Unloaded -= CardUnloaded;
            }
            trackedCard = card;
            trackedText = null;
            card.LayoutUpdated += ContentLayoutUpdated;
            card.Unloaded += CardUnloaded;
        }
    }
    private void CardLoaded(object sender, RoutedEventArgs args) {
        Border? card = currentCard();
        if (ReferenceEquals(sender, card)) ApplyCard(card);
    }
    private void ContentLayoutUpdated(object? sender, EventArgs args) {
        TextBlock? text = trackedCard?.Child as TextBlock;
        if (ReferenceEquals(text, trackedText)) return;
        trackedText = text;
        if (text is not null) text.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
    }
    private void CardUnloaded(object sender, RoutedEventArgs args) => trackedText = null;
}
