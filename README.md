A small app to allow switching audio playback devices when Steam Big Picture starts since Steam itself doesn't do this. Also you can turn off BT on app close to turn off your controller automatically when it is connected directly with bluetooth radio.
Since there's no xbox controller api available it's impossible to turn it off when it is connected with Xbox wireless adapter.

The app should support Win10 and Win11. Any feedback is appreciated.

TO-DO:

1. ~~Make it a tray app~~
2. ~~Launch on startup checkbox~~
3. ~~Switch audio to the selected device on BP start~~
4. ~~Turn off bluetooth checkbox on BP close~~
5. ~~Switch audio to the previous device on BP close~~
6. ~~Compile into a single .exe~~
7. ~~Check for a TV audio device to preset~~
8. ~~Save app state~~
9. ~~Turn Night Light off when BP starts~~
10. ~~Turn off Wireless Xbox controller (the way's a bit hacky, might break in the future)~~
11. Pause music when BP starts?
12. Adapt UI for 2K+ screens
13. Set autostart as admin
14. Option to dim LED of an Xbox Controller
15. Change audio only when BP is on TV
16. Make it not trigger ms defender?
17. Refactor the whole thing since C# is not my strong suit

For Night Light control functionality I thank this person: [Maclay74](https://github.com/Maclay74).
For helping with Wireless Xbox Series Controller power off - [Leclowndu93150](https://github.com/Leclowndu93150).
