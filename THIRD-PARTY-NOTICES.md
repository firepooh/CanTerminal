# Third-party notices

CanTerminal is distributed under the MIT License (see `LICENSE`). It uses the following
third-party components.

## DbcParserLib

Used to parse DBC databases and unpack signals.

- Author: Emanuel Feru
- Project: https://github.com/EFeru/DbcParser
- License: MIT

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## System.IO.Ports

Serial-port transport for the SLCAN adapter (WeAct USB2CANFDV2 and similar).

- Author: Microsoft (.NET libraries)
- Project: https://github.com/dotnet/runtime
- License: MIT

## .NET runtime

The standalone build embeds the .NET runtime, licensed by Microsoft under the MIT License.
https://github.com/dotnet/runtime

## icsneo40.dll — not distributed

Talking to a ValueCAN / neoVI needs Intrepid Control Systems' `icsneo40.dll`, which arrives with
their driver package and is **not** included here. CanTerminal loads whatever is installed on the
machine. Opening log files and using the virtual bus work without it.
