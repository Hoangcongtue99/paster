# icongen

Regenerates `paster.ico` in the repository root.

```powershell
dotnet run --project tools/icongen -- ../../paster.ico
```

Every size is rendered independently rather than downscaled from one large
bitmap — a 256px glyph squeezed to 16px turns to mush, so the small frames get
thinner padding and a proportionally larger letter.

The glyph is positioned by its own ink bounds rather than by `DrawString`'s
centring, which aligns the *line box* (ascent + descent). "P" has no descender,
so line-box centring leaves it visibly high.
