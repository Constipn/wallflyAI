# Third-party notices

Wallfly for Windows ships or downloads the following third-party components. Versions and SHA-256 hashes
of every downloaded file are pinned in `windows/tools/models.lock.json`; `windows/tools/fetch-models.ps1`
verifies them before anything is used.

Overview (details and license texts below):

| Component | Shipped as | License |
|---|---|---|
| WebRTC audio processing (AEC3, noise suppressor) | compiled into `WallflyAudio.dll` | BSD-3-Clause + WebRTC patent grant |
| JsonCpp (part of the vendored WebRTC snapshot) | not compiled; notice kept with the snapshot | Public Domain / MIT |
| whisper.cpp | `whisper\` | MIT |
| Whisper, Silero VAD model weights | `models\` | MIT |
| sherpa-onnx | `sherpa-onnx*.dll` | Apache-2.0 |
| ONNX Runtime | `onnxruntime.dll` | MIT |
| pyannote segmentation-3.0 | `models\diarization\` | MIT |
| 3D-Speaker ERes2Net | `models\diarization\` | Apache-2.0 |
| NAudio (`NAudio.Wasapi`, `NAudio.Core`) | `NAudio.*.dll` | MIT |
| FlaUI (`FlaUI.UIA3`, `FlaUI.Core`) | `FlaUI.*.dll` | MIT |
| Interop.UIAutomationClient | `Interop.UIAutomationClient.dll` | MIT |
| Lingua for .NET (`SearchPioneer.Lingua`) and its language models | `Lingua.dll`, `Lingua\LanguageModels\` | Apache-2.0 |
| CommunityToolkit.Mvvm | `CommunityToolkit.Mvvm.dll` | MIT |
| Concentus (Opus codec, managed) | `Concentus.dll` | BSD-style (Opus license) |
| Concentus.OggFile | `Concentus.Oggfile.dll` | MIT |
| Model Context Protocol C# SDK | `ModelContextProtocol*.dll` | Apache-2.0 |
| .NET runtime, Windows Desktop runtime, `Microsoft.Extensions.*`, `Microsoft.Win32.SystemEvents`, `System.Security.Cryptography.ProtectedData` | runtime and `*.dll` | MIT |

## WebRTC audio processing (echo canceller AEC3, noise suppressor) — WallflyAudio.dll

- Project: https://webrtc.googlesource.com/src — Copyright (c) 2011, The WebRTC project authors.
- Snapshot: `windows/native/vendor/webrtc` is a byte-identical copy of the macOS `Sources/WebRTCAEC` tree (AEC3 in
  `vendor/`, the noise suppressor in `vendor-ns/`); origin, commits and checksums in `PROVENANCE.md`,
  `PROVENANCE-NS.md`, `SHA256SUMS`, `SHA256SUMS-NS`. Compiled statically into `WallflyAudio.dll`; used unmodified.
- License: BSD 3-Clause (below), plus the WebRTC "Additional IP Rights Grant (Patents)" (below). `vendor/LICENSE` and
  `vendor-ns/LICENSE` are identical.
- `build.ps1 -Publish` also copies the original files to `third-party\webrtc\` (`LICENSE`, `JSONCPP-LICENSE`,
  `vendor-ns\LICENSE`, `vendor-ns\PATENTS`).

```
Copyright (c) 2011, The WebRTC project authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

```
Additional IP Rights Grant (Patents)

"This implementation" means the copyrightable works distributed by
Google as part of the WebRTC code package.

Google hereby grants to you a perpetual, worldwide, non-exclusive,
no-charge, irrevocable (except as stated in this section) patent
license to make, have made, use, offer to sell, sell, import,
transfer, and otherwise run, modify and propagate the contents of this
implementation of the WebRTC code package, where such license applies
only to those patent claims, both currently owned by Google and
acquired in the future, licensable by Google that are necessarily
infringed by this implementation of the WebRTC code package. This
grant does not include claims that would be infringed only as a
consequence of further modification of this implementation. If you or
your agent or exclusive licensee institute or order or agree to the
institution of patent litigation against any entity (including a
cross-claim or counterclaim in a lawsuit) alleging that this
implementation of the WebRTC code package or any code incorporated
within this implementation of the WebRTC code package constitutes
direct or contributory patent infringement, or inducement of patent
infringement, then any patent rights granted to you under this License
for this implementation of the WebRTC code package shall terminate as
of the date such litigation is filed.
```

### JsonCpp (inside the WebRTC snapshot, not compiled)

- The vendored WebRTC snapshot contains JsonCpp 0.6 (`vendor/base/jsoncpp`). `windows/native/vendor/CMakeLists.txt`
  drops it (and the two JSON config readers) from the build, so no JsonCpp code is in `WallflyAudio.dll`.
- Its notice travels with the snapshot: Public Domain, or where that is not recognized MIT License,
  Copyright (c) 2007-2010 Baptiste Lepilleur (`JSONCPP-LICENSE`, copied to `third-party\webrtc\`).

## whisper.cpp (whisper-server.exe, whisper.dll, ggml*.dll)

- Project: https://github.com/ggml-org/whisper.cpp
- Version: v1.9.5 (release build b5454, commit `d1be6fde11ac6e0407606b4e42fe72d34add8037`),
  asset `whisper-bin-x64.zip` (CPU build for Windows x64)
- Files used: `whisper-server.exe`, `whisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu-*.dll`
  (other executables and `SDL2.dll`, `llama.dll`, `parakeet.dll` from the archive are not shipped)
- Runs as a local child process bound to 127.0.0.1 only; audio never leaves the PC.
- Requires the Microsoft Visual C++ 2015-2022 x64 runtime (`MSVCP140.dll`, `VCRUNTIME140.dll`,
  `VCRUNTIME140_1.dll`, `VCOMP140.dll`), which is not part of the archive. If it is bundled app-locally,
  the Microsoft Visual C++ Redistributable license terms apply to those files.
- License: MIT

```
MIT License

Copyright (c) 2023-2026 The ggml authors

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

## Whisper model weights (ggml-large-v3-turbo.bin, ggml-tiny.bin)

- Original models: OpenAI Whisper, https://github.com/openai/whisper — MIT License, Copyright (c) 2022 OpenAI
  (the model weights are released under the same MIT License as the code).
- ggml conversion: https://huggingface.co/ggerganov/whisper.cpp (revision
  `5359861c739e955e79d9a303bcbc70fb988958b1`), published under the MIT License.
- `ggml-large-v3-turbo.bin` is the default local transcription model. `ggml-tiny.bin` is fetched only for the
  gated developer test (`fetch-models.ps1 -TestModel`) and is not shipped.
- The MIT License text above applies with the copyright line "Copyright (c) 2022 OpenAI".

## Silero VAD (ggml-silero-v5.1.2.bin)

- Original model: Silero VAD v5.1.2, https://github.com/snakers4/silero-vad — MIT License,
  Copyright (c) 2020-present Silero Team.
- ggml conversion: https://huggingface.co/ggml-org/whisper-vad (revision
  `9ffd54a1e1ee413ddf265af9913beaf518d1639b`), published under the MIT License.
- Used only by whisper-server (`--vad --vad-model`) when no word times are needed.
- The MIT License text above applies with the copyright line "Copyright (c) 2020-present Silero Team".

## sherpa-onnx (sherpa-onnx.dll, sherpa-onnx-c-api.dll) — speaker diarization

- Project: https://github.com/k2-fsa/sherpa-onnx (Next-gen Kaldi), Copyright 2019-2023 Xiaomi Corporation (as stated in the NuGet package)
- Version: NuGet `org.k2fsa.sherpa.onnx` 2.0.1 (managed API) and `org.k2fsa.sherpa.onnx.runtime.win-x64` 2.0.1
  (native, commit `af2d8d81dbfa61c4cea4c568da78f3982edca005`), referenced by `Wallfly.Diarization`.
- Used for the offline acoustic speaker diarization after a recording stops (local, no network).
- License: Apache License 2.0 (full text below). The project has no NOTICE file.

## ONNX Runtime (onnxruntime.dll, shipped inside the sherpa-onnx win-x64 runtime package)

- Project: https://github.com/microsoft/onnxruntime — Copyright (c) Microsoft Corporation.
- License: MIT; the MIT License text above applies with the copyright line "Copyright (c) Microsoft Corporation".

## pyannote segmentation-3.0 (models/diarization/sherpa-onnx-pyannote-segmentation-3-0/model.onnx)

- Original model: pyannote/segmentation-3.0, https://huggingface.co/pyannote/segmentation-3.0 (Hervé Bredin,
  Alexis Plaquet; pyannote.audio). MIT License, Copyright (c) 2022 CNRS. The license file is kept next to the model.
- ONNX export: k2-fsa/sherpa-onnx, release `speaker-segmentation-models`, archive
  `sherpa-onnx-pyannote-segmentation-3-0.tar.bz2` (only `model.onnx` and `LICENSE` are extracted).
- The MIT License text above applies with the copyright line "Copyright (c) 2022 CNRS".
- Note: the macOS version uses pyannote *community-1* (CC-BY-4.0) through FluidAudio; Windows uses segmentation-3.0.

## 3D-Speaker ERes2Net speaker embedding (models/diarization/3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx)

- Model: ERes2Net speaker verification model trained on VoxCeleb, from 3D-Speaker (Alibaba DAMO Academy),
  https://github.com/modelscope/3D-Speaker, model card https://modelscope.cn/models/iic/speech_eres2net_sv_en_voxceleb_16k
  (paper: Y. Chen et al., "An Enhanced Res2Net with Local and Global Feature Fusion for Speaker Verification",
  Interspeech 2023). ONNX export as published by k2-fsa/sherpa-onnx, release `speaker-recongition-models`. Used unmodified.
- License: Apache License 2.0 (as declared on the model card; full text below).
- Training data: VoxCeleb1/2 (A. Nagrani, J. S. Chung, A. Zisserman et al., https://www.robots.ox.ac.uk/~vgg/data/voxceleb/,
  CC BY 4.0).

### Apache License 2.0 (sherpa-onnx, 3D-Speaker ERes2Net, Lingua for .NET, Model Context Protocol C# SDK)

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

## Model Context Protocol C# SDK (ModelContextProtocol.dll, ModelContextProtocol.Core.dll) — wallfly-mcp.exe

- Project: https://github.com/modelcontextprotocol/csharp-sdk — © Model Context Protocol a Series of LF Projects, LLC.
- Version: NuGet `ModelContextProtocol` 2.3.0 and `ModelContextProtocol.Core` 2.3.0
  (commit `9a327829ef066c44a0d737c58bf3c7617d7c701c`), referenced by `Wallfly.Mcp`.
- Used only by the local, read-only MCP server `wallfly-mcp.exe` (stdio, no network listener).
- License: Apache License 2.0 (full text in the section "Apache License 2.0" above).

## NAudio (NAudio.Wasapi.dll, NAudio.Core.dll)

- Project: https://github.com/naudio/NAudio — © Mark Heath 2023.
- Version: NuGet `NAudio.Wasapi` 2.2.1 (with `NAudio.Core` 2.2.1), referenced by `Wallfly.Teams` (microphone session
  probe of Wallfly Detector) and the gated hardware test.
- License: MIT; the MIT License text above applies with the copyright line "Copyright (c) 2023 Mark Heath".

## FlaUI (FlaUI.UIA3.dll, FlaUI.Core.dll)

- Project: https://github.com/FlaUI/FlaUI — Copyright (c) 2016-2024 (FlaUI contributors).
- Version: NuGet `FlaUI.UIA3` 5.0.0 (with `FlaUI.Core` 5.0.0), referenced by `Wallfly.Teams` (Teams call detection
  and speaker names over UI Automation; read-only).
- License: MIT; the MIT License text above applies with the copyright line "Copyright (c) 2016-2024".

## Interop.UIAutomationClient (Interop.UIAutomationClient.dll)

- Project: https://github.com/Roemer/UIAutomation-Interop — Copyright (c) 2019 Roman.
- Version: NuGet `Interop.UIAutomationClient` 10.19041.0, referenced by `Wallfly.Teams` (COM interop for UI Automation).
- License: MIT (license file of the package); the MIT License text above applies with the copyright line
  "Copyright (c) 2019 Roman".

## Lingua for .NET (Lingua.dll and Lingua\LanguageModels\) — language detection

- Project: https://github.com/searchpioneer/lingua-dotnet — Search Pioneer; a .NET port of Lingua by Peter M. Stahl
  (https://github.com/pemistahl/lingua, https://github.com/pemistahl/lingua-rs).
- Version: NuGet `SearchPioneer.Lingua` 2.0.0, referenced by `Wallfly.Ai` (language check of the enhanced notes); the
  trigram language models from the package are shipped next to it.
- License: Apache License 2.0 (full text in the section "Apache License 2.0" above).

## CommunityToolkit.Mvvm (CommunityToolkit.Mvvm.dll)

- Project: https://github.com/CommunityToolkit/dotnet — (c) .NET Foundation and Contributors.
- Version: NuGet `CommunityToolkit.Mvvm` 8.4.2, referenced by `Wallfly.App`.
- License: MIT; the MIT License text above applies with the copyright line "Copyright (c) .NET Foundation and Contributors".

## Concentus (Concentus.dll) — Opus encoder for the cloud speaker separation

- Project: https://github.com/lostromb/concentus — a pure C# port of the Opus reference codec by Logan Stromberg;
  © Xiph.Org Foundation, Skype Limited, CSIRO, Microsoft Corp.
- Version: NuGet `Concentus` 2.2.3, referenced by `Wallfly.Diarization` (`DeepgramPrerecordedDiarizer` compresses the
  Others track to Ogg/Opus after the meeting before uploading it to Deepgram).
- License: the Opus license (BSD-style), the package's `LICENSE` file verbatim:

```
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```

## Concentus.OggFile (Concentus.Oggfile.dll)

- Project: https://github.com/lostromb/concentus.oggfile — Copyright © Logan Stromberg, Andrew Ward.
- Version: NuGet `Concentus.OggFile` 1.0.7, referenced by `Wallfly.Diarization` (Ogg container for the Opus upload).
- License: MIT (license expression of the package); the MIT License text above applies with the copyright line
  "Copyright © Logan Stromberg, Andrew Ward".

## .NET runtime, Windows Desktop runtime and Microsoft.Extensions libraries

- Projects: https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms,
  https://github.com/dotnet/extensions — Copyright (c) .NET Foundation and Contributors.
- Shipped self-contained in `dist\Wallfly` (one copy shared by Wallfly.exe, Wallfly Detector.exe and wallfly-mcp.exe):
  .NET runtime 10.0.13 (`Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App`), `Microsoft.Extensions.Hosting`
  10.0.13 with its dependencies (`Microsoft.Extensions.*`), `Microsoft.Extensions.AI.Abstractions` 10.11.0 (dependency of
  the MCP SDK), `Microsoft.Win32.SystemEvents` 10.0.0 (sleep/resume in `Wallfly.Teams`) and
  `System.Security.Cryptography.ProtectedData` 10.0.0 (one-time import of the first port's DPAPI key in `Wallfly.Core`).
- License: MIT; the MIT License text above applies with the copyright line "Copyright (c) .NET Foundation and Contributors".
- The runtime packages' own notices are copied by `build.ps1 -Publish` to
  `third-party\microsoft.netcore.app\` (`THIRD-PARTY-NOTICES.TXT`, `LICENSE.TXT`) and `third-party\microsoft.windowsdesktop.app\`
  (`LICENSE`; that package ships no separate third-party notice file).