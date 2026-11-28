// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Automation;
using PCLCS.Validation;

namespace PCLLF.Wpf.Glass;

/// <summary>A native editor that can use the launcher's existing validation rules.</summary>
public sealed class GlassValidatedTextBox : TextBox
{
    public Collection<LegacyValidationRule> ValidateRules { get; } = new();
    public string ValidateResult { get; private set; } = "";
    public bool IsValidated => ValidateResult.Length == 0;

    public bool ValidateInput()
    {
        ValidateResult = LegacyValidation.Validate(Text, ValidateRules);
        AutomationProperties.SetHelpText(this, ValidateResult);
        if (!IsValidated)
            SetResourceReference(BorderBrushProperty, "GlassPresentationWarnEdge");
        else
            ClearValue(BorderBrushProperty);
        return IsValidated;
    }

    public void ClearValidationFeedback()
    {
        ClearValue(BorderBrushProperty);
        AutomationProperties.SetHelpText(this, "");
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        ValidateInput();
        base.OnTextChanged(e);
    }
}
