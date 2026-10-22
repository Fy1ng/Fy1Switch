# Application artwork and UI icons

`branding/app-icon-source.png` is the user-approved application artwork, copied
verbatim from the supplied PNG. It is separate from the licensed Fluent UI icons.
Run `python tools/Generate-AppIcons.py` with Pillow to regenerate Windows ICO/PNG,
Android density variants, adaptive foreground and Android 13 themed silhouette.
The generated icons only fit/resize the original artwork; the adaptive foreground
keeps the entire monitor inside the safe circle. The source retains its original
pixels and transparency. The themed silhouette uses luminance and alpha from that
same image, so its light switch becomes negative space.

UI symbols use Microsoft's Fluent System Icons, Regular, pinned to
`83b26348c811f072429427ff48362238656524fe`:

- Source: https://github.com/microsoft/fluentui-system-icons/tree/83b26348c811f072429427ff48362238656524fe
- License: `licenses/Fluent-System-Icons.LICENSE`, copied into Android assets and
  Windows packages. Keep it with distributed binaries.
- Windows: `tools/Import-FluentIcons.ps1` imports only the 19 selected SVG files.
  The 20px variants serve normal controls; 16px variants serve window controls.
  `VectorIcon` draws their original paths using the surrounding foreground color.
- Android: `ic_fluent_*_24_regular.xml` files come from
  `android/library/src/main/res/drawable/` at that revision. The only change is
  replacing `@color/fluent_default_icon_tint` with `#FFFFFFFF`; Compose provides
  the actual foreground. `ic_display*.xml` reuse the Desktop, Desktop Off and
  Desktop Sync paths for single-color Quick Settings icons.

Application identity and state remain distinct: the full-color artwork is the
launcher/executable/window icon; the Windows tray uses separate green/blue/amber
monitor symbols for on/off/unknown, and Android Quick Settings retains
state-specific monochrome symbols. Both UI
clients retain text labels for critical actions and distinguish unknown state.
