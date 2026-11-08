namespace PCL
{
    // Only LF original XAML/type/Handles/model identity adapters; all workflows are C#.
    public partial class PageOtherTest
    {
        private void BtnJrrp_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Jrrp();
        public static void Jrrp()
        {
            AuxiliaryPageRuntime.Tools.Jrrp();
        }

        public static string GetRandomCave()
        {
            return AuxiliaryPageRuntime.Tools.GetRandomCave();
        }

        public static string GetRandomHint()
        {
            return AuxiliaryPageRuntime.Tools.GetRandomHint();
        }

        public static string GetRandomPresetHint()
        {
            return AuxiliaryPageRuntime.Tools.GetRandomPresetHint();
        }
    }
}
