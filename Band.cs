// Windows 10 taskbar deskband host -- deskbands were removed in Win11, so Win10 is the
// only supported OS. CSDeskBand does the IDeskBand2 plumbing; the UI is in PlayerControl.

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
            var player = new PlayerControl { Dock = DockStyle.Fill };
            BackColor = player.BandColor;  // match the taskbar so no gray sliver shows behind the child
            player.ThemeChanged += () => BackColor = player.BandColor;  // follow a light/dark switch
            Controls.Add(player);
        }
    }
}
