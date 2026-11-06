# Third-party notices

OpenSCCT Editor includes the components below. Release packages also include the Godot Engine (MIT, with its own
third-party components) and the .NET runtime (MIT); their full notices are added to `THIRD-PARTY-NOTICES.txt` in
each release.

The editor is built with libraries from [OpenSCCT](https://github.com/Lumbridge/OpenSCCT), the same project. Through
them it also includes the packages BouncyCastle.Cryptography, Concentus and LiteNetLib, listed below.

## libwebp

`src/OpenScct.Editor.Core/Identity/Codecs/Vp8Tables.cs` holds constant tables taken from libwebp 1.6.0 (the copy
bundled with Pillow 12), and `Vp8Decoder.cs` follows libwebp's decoder where the VP8 format leaves a choice (its
chroma upsampling), so a picture decodes as libwebp decodes it. libwebp is distributed under this licence (Google
also grants a patent licence for VP8, in libwebp's
[`PATENTS`](https://github.com/webmproject/libwebp/blob/v1.6.0/PATENTS) file):

```
Copyright (c) 2010, Google Inc. All rights reserved.

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

## SCCT Versus Reloaded Editor (RE+)

The compiled-map brush recovery in `src/OpenScct.Editor.Core/Interop/Sdc/Recovery/` (`BrushRecovery.cs`,
`CellGeometry.cs`, `FacePartition.cs`) follows RE+'s recovery (`RecoveredBspGeometry`, `RecoveredSurfacePartition`):
its steps, ordering and tolerances. `src/OpenScct.Editor.Core/Interop/ReConveyors.cs` follows RE+ 2.8.0's conveyor
belt model (`ConveyorModel.h`) and its limits. `src/OpenScct.Editor.Core/Assets/AssetGlossary.cs`'s built-in word
list draws on RE+'s `tools/asset_names/fr-en.txt`, and `src/OpenScct.Editor.Core/Gameplay/CameraNetworks.cs` follows
RE+'s camera network model (`CameraNetworkModel.h`). The emitter catalogue metadata in
`src/OpenScct.Editor.Core/Emitters/StockEmitters.json` follows `tools/emitter_library/defaults_manifest.json`. The
editor reads the particle data and textures from installed OpenSCCT content. RE+ ([source](https://github.com/Lumbridge/SCCT_Versus_Reloaded_Editor))
is distributed under this licence:

```
MIT License

Copyright (c) 2024 AllyPal

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

## BouncyCastle.Cryptography 2.6.2

```
Copyright (c) 2000-2025 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org).
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Concentus 2.2.2

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

## LiteNetLib 2.1.4

```
MIT License

Copyright (c) 2025 Ruslan Pyrch

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

## libjpeg behaviour

`src/OpenScct.Editor.Core/Identity/Codecs/JpegDecoder.cs` follows the output of the Independent JPEG Group's libjpeg
(its chroma upsampling and rounding, progressive refinement) so a picture decodes as libjpeg decodes it. No libjpeg
code is included.
