#if DEMO
// Synthetic, CCTV-looking test scenes for the demo, screenshots and GIFs (no real camera footage). Ported from
// the Linux project's tools/demo/scenes.py: flat boxes (buildings, doors, fences, a moving car or person), sensor
// noise and an NVR-style on-screen display. Night scenes are greyscale with heavier noise, like infrared.
//
// Only the demo build (build.ps1 demo, /define:DEMO) accepts `url = demo:<scene>`; it is played by libmpv's lavfi
// input, so no RTSP server, Python or ffmpeg is needed.
using System;
using System.Collections.Generic;
using System.Text;

namespace CctvOverlay
{
    internal static class DemoScenes
    {
        const int W = 960, H = 540;
        const string Font = "fontfile='C\\:/Windows/Fonts/consolab.ttf'";

        static string Box(int x, int y, int w, int h, string color, string t = "fill")
        {
            return string.Format("drawbox=x={0}:y={1}:w={2}:h={3}:color={4}:t={5}", x, y, w, h, color, t);
        }

        static string Sky(string c0, string c1, int horizon)
        {
            return string.Format("gradients=s={0}x{1}:r=25:c0={2}:c1={3}:x0=0:y0=0:x1=0:y1={4}:speed=0.0001:n=2", W, H, c0, c1, horizon);
        }

        static string Finish(string sky, List<string> p, string name, int n, bool night)
        {
            if (night)
            {
                p.Add("hue=s=0");
                p.Add("eq=brightness=0.02:contrast=1.15");
                p.Add("noise=alls=16:allf=t+u");
            }
            else
            {
                p.Add("noise=alls=5:allf=t+u");
            }
            string f = Font + ":fontcolor=white:fontsize=22:shadowx=2:shadowy=2:shadowcolor=black@0.7";
            p.Add(string.Format("drawtext={0}:text='CH{1:00}  {2}':x=18:y=h-th-18", f, n, name.ToUpperInvariant()));
            p.Add("drawtext=" + f + ":text='%{localtime\\:%Y-%m-%d %X}':x=w-tw-18:y=18");
            return sky + "," + string.Join(",", p.ToArray());
        }

        static string Front()
        {
            var p = new List<string> {
                Box(0, 300, W, H - 300, "0x6e7a5a"),
                Box(140, 90, 680, 300, "0xd9cbb0"),
                Box(120, 70, 720, 28, "0x5a4a42"),
                Box(430, 200, 100, 190, "0x6b3e26"),
                Box(510, 290, 10, 10, "0xd8b04a"),
                Box(200, 160, 150, 110, "0x9fc3d8"), Box(200, 160, 150, 110, "0xffffff", "6"),
                Box(610, 160, 150, 110, "0x9fc3d8"), Box(610, 160, 150, 110, "0xffffff", "6"),
                Box(400, 390, 160, 26, "0x9a9a96"),
                Box(380, 416, 200, 124, "0xb8b3a8"),
                "drawbox=x='455+130*sin(t/2.2)':y='330-20*abs(sin(t/2.2))':w=46:h=120:color=0x2d3b55:t=fill",
                "drawbox=x='463+130*sin(t/2.2)':y='300-20*abs(sin(t/2.2))':w=30:h=32:color=0xe0b89a:t=fill",
            };
            return Finish(Sky("0x8fb8de", "0xe5edf2", 300), p, "Front Door", 1, false);
        }

        static string Driveway()
        {
            var p = new List<string> {
                Box(0, 250, W, H - 250, "0x7d8a63"),
                Box(0, 250, W, 90, "0x3d3f42"),
                Box(0, 292, W, 6, "0xe8e2c8"),
                Box(330, 340, 330, 200, "0xa9a69f"),
                Box(560, 120, 300, 130, "0xcfc7b6"),
                Box(590, 150, 240, 100, "0xf2f0ea"), Box(590, 175, 240, 3, "0xbab6aa"), Box(590, 205, 240, 3, "0xbab6aa"),
                "drawbox=x='mod(t*170\\,iw+320)-320':y=262:w=230:h=60:color=0xb3262b:t=fill",
                "drawbox=x='mod(t*170\\,iw+320)-270':y=246:w=120:h=26:color=0x2a3540:t=fill",
                "drawbox=x='mod(t*170\\,iw+320)-290':y=312:w=36:h=20:color=0x111111:t=fill",
                "drawbox=x='mod(t*170\\,iw+320)-150':y=312:w=36:h=20:color=0x111111:t=fill",
                Box(380, 380, 240, 110, "0xe9ecef"), Box(410, 350, 170, 45, "0x3a4a5a"),
            };
            return Finish(Sky("0x9cc2e0", "0xdde7ee", 250), p, "Driveway", 2, false);
        }

        static string Backyard()
        {
            var p = new List<string> { Box(0, 280, W, H - 280, "0x3c3c3c"), Box(0, 150, W, 14, "0x8a8a8a"), Box(0, 250, W, 14, "0x8a8a8a") };
            for (int x = 10; x < W; x += 70)
                p.Add(Box(x, 130, 34, 160, "0x9c9c9c"));
            p.Add(Box(90, 330, 220, 120, "0x5a5a5a"));
            p.Add(Box(700, 300, 160, 150, "0x2a2a2a"));
            p.Add("drawbox=x='mod(t*55\\,iw+120)-120':y=470:w=70:h=26:color=0xdddddd:t=fill");
            p.Add("drawbox=x='mod(t*55\\,iw+120)-60':y=455:w=22:h=22:color=0xdddddd:t=fill");
            return Finish(Sky("0x1a1a1a", "0x444444", 280), p, "Backyard", 3, true);
        }

        static string Garage()
        {
            var p = new List<string> {
                Box(0, 380, W, H - 380, "0x8b8a85"),
                Box(40, 60, 260, 12, "0x6a5038"), Box(40, 140, 260, 12, "0x6a5038"), Box(40, 220, 260, 12, "0x6a5038"),
                Box(60, 30, 40, 30, "0x2f6fa8"), Box(120, 110, 60, 30, "0xc0802a"), Box(200, 190, 50, 30, "0x3a8a4a"),
                Box(640, 250, 280, 130, "0x6a5038"),
                Box(330, 260, 300, 150, "0x2b2f36"), Box(370, 210, 210, 60, "0x48607a"),
                "eq=brightness='0.03*sin(t*3)':eval=frame",
            };
            return Finish(Sky("0xb9b5aa", "0x8e8a80", 380), p, "Garage", 4, false);
        }

        static string Alley()
        {
            var p = new List<string> {
                Box(0, 0, 260, H, "0x2b2b2b"), Box(700, 0, 260, H, "0x262626"),
                Box(260, 330, 440, H - 330, "0x4a4a4a"),
                Box(420, 40, 120, 30, "0xf5f5f5"),
                Box(130, 380, 60, 80, "0x3a3a3a"), Box(760, 360, 70, 100, "0x3a3a3a"),
                "drawbox=x='480+90*sin(t/3)':y='300+60*sin(t/1.5)^2':w=40:h=110:color=0x101010:t=fill",
            };
            return Finish(Sky("0x202020", "0x3a3a3a", 330), p, "Side Alley", 5, true);
        }

        public static bool IsScene(string url)
        {
            return url.StartsWith("demo:", StringComparison.Ordinal) && Graph(url.Substring(5)) != null;
        }

        static string Graph(string scene)
        {
            switch (scene)
            {
                case "front": return Front();
                case "driveway": return Driveway();
                case "backyard": return Backyard();
                case "garage": return Garage();
                case "alley": return Alley();
            }
            return null;
        }

        /// <summary>The libmpv URL for a demo camera. Every scene has a quiet "wind" audio track, so the speaker
        /// icon shows sound in the single view.</summary>
        public static byte[] Url(string url)
        {
            string g = Graph(url.Substring(5));
            return Encoding.UTF8.GetBytes("av://lavfi:" + g + "[out0];anoisesrc=color=brown:amplitude=0.02:r=48000[out1]");
        }
    }
}
#endif
