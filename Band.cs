// Windows 10 taskbar deskband host for the mini media player.
//
// A COM shell extension (CSDeskBand does the IDeskBand2 plumbing) that docks
// inside the Win10 taskbar and hosts a PlayerControl (all the actual UI + SMTC
// logic lives there, shared with the Win11 standalone app in app/AppBarForm.cs).
//
// Deprecated tech: deskbands work on Windows 10, but were removed in Windows 11.
// Build:     dotnet build -c Release
// Register:  register.bat   (self-elevates; runs RegAsm /codebase)
// Enable:    right-click the taskbar -> Toolbars -> Mini Player

using System.Runtime.InteropServices;
using System.Windows.Forms;
using CSDeskBand;
using CSDeskBand.Win;

namespace MiniPlayerBand
{
    [ComVisible(true)]
    [Guid("D7B2E4A1-3F56-4C8B-9E0D-2A6C1F5B8E44")]  // keep stable across rebuilds (it is the COM CLSID)
    [CSDeskBandRegistration(Name = "Mini Player", ShowDeskBand = true)]
    public class Band : CSDeskBandWin
    {
        public Band()
        {
            Options.Title = "Mini Player";
            Options.ShowTitle = false;
            Options.MinHorizontalSize = new CSDeskBand.Size(150, 20);
            Options.HorizontalSize = new CSDeskBand.Size(150, 40);  // fixed width (min == desired) so it does not auto-resize
            BackColor = PlayerControl.TaskbarColor();  // match the taskbar so no gray sliver shows behind the child
            Controls.Add(new PlayerControl { Dock = DockStyle.Fill });
        }
    }
}
