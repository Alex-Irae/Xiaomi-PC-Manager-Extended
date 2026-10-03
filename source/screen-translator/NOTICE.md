# Source attribution

Screen Translator is an independent project and is not affiliated with Xiaomi,
Intel, or Click'n'Translate. Its name is a temporary generic label.

`screen_translator/reading_order.py` and `screen_translator/windows_surface.py`
are derived from the supplied Click'n'Translate 1.8.1 source, by Jabrail Khalil
and contributors: https://github.com/jabrailkhalil/clickntranslate.
They are bundled locally; no upstream checkout, installation, configuration,
or running process is required. The project retains GPL-3.0 licensing in LICENSE.

OCR assets originate in PP-OCR/PaddleOCR and RapidOCR; their model licenses and
exact revisions are recorded in the model manifest. Translation model licensing
and conversion provenance are recorded there separately. These notices are
source attribution, not product branding or an endorsement.

The offline package copies the existing Python standard library/runtime (PSF
license), .NET runtime (MIT, including ThirdPartyNotices), WebView2 SDK assemblies
(Microsoft WebView2 SDK terms), and installed inference distributions. Python
and .NET license files and distribution metadata/license files are retained in
the payload. WebView2 Runtime remains an existing Windows dependency. Source
for the native host, launcher and installer is included under source/; Python
backend and frontend source are included directly. No third-party app executable
or reference-app installation is bundled.
