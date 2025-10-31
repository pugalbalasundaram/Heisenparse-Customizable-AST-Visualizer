using HeisenParserWPF.Services;
using System.Threading.Tasks;
using System.Windows;

namespace HeisenParserWPF
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Initialize Supabase client at app startup
            await SupabaseClientManager.InitializeAsync();
        }
    }
}
