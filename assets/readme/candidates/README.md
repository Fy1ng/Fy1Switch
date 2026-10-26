# README hero candidates

These are local alternatives for review, not replacements for the published
Chinese or English README. Open `index.html` in a browser to compare all three
designs and switch languages. Each exported hero is 1200x440 PNG.

| Variant | Artwork | Treatment | License |
| --- | --- | --- | --- |
| A | 3dicons v1 Computer and Mobile | Gradient devices on a dark background | CC0-1.0 |
| B | Microsoft Fluent Emoji Desktop computer and Mobile phone | 3D devices on a light background | MIT |
| C | illlustrations 113 Workstation | Original desktop scene with cropped empty margins | MIT |

The artwork is decorative. In particular, the music player and speakers in C
belong to the original illustration and do not represent Fy1Switch features.
All variants preserve the approved application icon and README body outside
the hero. The application and real device state are not changed by this work.

## Sources and licenses

Unmodified upstream images, the original SVG for C, and full license texts are
in `sources/`. `sources.json` records the original URLs and SHA-256 hashes.
`renders.json` records the generated PNG hashes. No model-generated device
artwork is used.

- [3dicons v1 collection](https://3dicons.co/collection/b68cf8-v1), by Vijay Verma.
  Asset URLs come from [Computer metadata](https://github.com/realvjy/3dicons/blob/8884d59e68a7bae0e0e2163af5b6c2ad992c01c8/content/3dicons-meta/computer.md)
  and [Mobile metadata](https://github.com/realvjy/3dicons/blob/8884d59e68a7bae0e0e2163af5b6c2ad992c01c8/content/3dicons-meta/mobile.md).
  CC0 text: [sources/3dicons.LICENSE](sources/3dicons.LICENSE).
- [Microsoft Fluent Emoji](https://github.com/microsoft/fluentui-emoji/tree/1ffb34c752ecf5d402f04cfb4b392c77f57c54bc).
  Copyright Microsoft Corporation. MIT text:
  [sources/Fluent-Emoji.LICENSE](sources/Fluent-Emoji.LICENSE).
- [illlustrations](https://github.com/realvjy/illlustrations/tree/b6d6f3286995e6efbbf397eff21b572245c336fd).
  Copyright 2015 Vijay Verma. MIT text:
  [sources/illlustrations.LICENSE](sources/illlustrations.LICENSE).

Reproduce the six exports on Windows with Python, Pillow, Segoe UI and
Microsoft YaHei installed:

```powershell
python assets/readme/candidates/compose.py
```

The composition only crops transparent or empty margins, resizes upstream
images, adds background and text, and exports PNG. The source files remain
unchanged. These alternatives are not included in the normal Windows package:
the existing publisher only copies files directly under `assets/readme/`.
