# Third-party software / Сторонние проекты

Obkhodiki is built on the work of these projects. Thank you to their authors.
Obkhodiki построен на работе этих проектов — спасибо их авторам.

## Downloaded at run time (not included in the Obkhodiki package)

The app downloads these from their official GitHub releases, verifies the SHA-256 digest GitHub publishes and runs
them unmodified. Their licenses apply to them; see the linked repositories for full texts and sources.

| Project | Author | License | Used for |
|---|---|---|---|
| [zapret](https://github.com/bol-van/zapret) (winws) | bol-van | MIT | DPI bypass engine |
| [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) | Flowseal | MIT | Strategies and the Windows build of zapret |
| [WinDivert](https://github.com/basil00/WinDivert) | basil00 | LGPL-3.0 or GPL-2.0 | Packet capture driver (shipped inside the zapret build) |
| [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) | Flowseal | MIT | Telegram proxy |
| [sing-box](https://github.com/SagerNet/sing-box) | nekohasekai (SagerNet) | GPL-3.0-or-later | VPS tunnel |
| [amnezia-box](https://github.com/amnezia-vpn/amnezia-box) | Amnezia VPN (sing-box fork) | GPL-3.0-or-later | AmneziaWG tunnel; built unmodified from source by `.github/workflows/amnezia-box.yml` |
| [sing-geosite](https://github.com/SagerNet/sing-geosite), [sing-geoip](https://github.com/SagerNet/sing-geoip) | nekohasekai (SagerNet) | GPL-3.0-or-later | Routing rule-sets (category-ru, geoip-ru, YouTube, Discord, …) |
| [russia-v2ray-rules-dat](https://github.com/runetfreedom/russia-v2ray-rules-dat) | runetfreedom | GPL-3.0 | Routing rule-sets (ru-blocked) |

## Included in the Obkhodiki package

| Project | Author | License |
|---|---|---|
| [WPF UI](https://github.com/lepoco/wpfui) | Leszek Pomianowski and WPF UI Contributors | MIT |
| [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet) (CommunityToolkit.Mvvm) | .NET Foundation and Contributors | MIT |
| [.NET](https://github.com/dotnet/runtime) (System.ServiceProcess.ServiceController, System.Diagnostics.EventLog) | .NET Foundation and Contributors | MIT |

### WPF UI

```
MIT License

Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors. https://lepo.co/

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### .NET Community Toolkit

```
# .NET Community Toolkit

Copyright © .NET Foundation and Contributors

All rights reserved.

## MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### .NET

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
