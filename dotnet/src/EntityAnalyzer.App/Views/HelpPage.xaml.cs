using System.Windows.Controls;

namespace EntityAnalyzer.App.Views;

public partial class HelpPage : UserControl
{
    /// <summary>Учебный JSON паттерна «Год Красной Луны» для туториала.</summary>
    public const string PeriodExampleJson =
        """
        [
          { "kind": "any_of", "options": [
              { "kind": "literal", "value": "год" },
              { "kind": "literal", "value": "года" },
              { "kind": "literal", "value": "году" },
              { "kind": "literal", "value": "час" },
              { "kind": "literal", "value": "часа" },
              { "kind": "literal", "value": "эра" },
              { "kind": "literal", "value": "эры" },
              { "kind": "literal", "value": "век" }
          ]},
          { "kind": "capitalized", "min": 1, "max": 3 }
        ]
        """;

    public HelpPage() => InitializeComponent();
}
