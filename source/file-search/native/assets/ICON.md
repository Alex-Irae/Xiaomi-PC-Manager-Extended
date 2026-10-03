# Xiaomi silver application icon

Input: `XiaoaiAgent/3.0.4.289/Assets/Logo.ico`, preserved as `xiaomi-original.ico`.

The built-in imagegen tool edited its PNG preview into `xiaomi-silver.png`, preserving transparency. `native/build-icon.ps1` encodes the Windows multi-size `app.ico` from this asset. The compiled launcher, native apphost, windows, tray and installer use that icon. User profile artwork is separate.

Final prompt:

> Edit target: the provided Xiaomi Logo.ico converted to PNG. Make a production Windows app icon of this exact logo in glossy silver, white and dark charcoal monochrome. Preserve its three interlocking curved ribbon/swirl geometry, outer circular proportions, curved end caps, the relative ribbon order and the negative spaces as faithfully as possible. Replace orange, blue and pink with restrained metallic silver gradients and subtle white specular highlights, dark graphite shading on the ribbons only. Clean smooth edges, no text, no tile, no frame, no new objects, no dramatic 3D extrusion. Keep all areas outside and between the ribbons fully transparent, with no background shadows. Centered square icon, tight composition like the original with ~4% margin. It must remain legible at 16 and 32 pixels.
