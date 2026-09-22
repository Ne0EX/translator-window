# Local live translator

A translator for text visible on the user's screen. Capture choice and subtitle
presentation are independent.

## Language

**Selected area**: A rectangle chosen by the user in desktop coordinates.

**Window**: A selected visible application window whose position is tracked.

**Screen**: A selected display's visible content.

**Subtitle overlay**: Translated text displayed beside its original text.
_Avoid_: Overwrite

**Subtitle overwrite**: Translated text displayed over an opaque cover on the
original text. It changes the presentation, never the source content.

**Local processing**: Recognition and translation performed on the user's own
computer with local models. Captured images and recognized text are not sent to
translation services.

**Source language**: The language of the visible text. Korean, Japanese, English,
and Thai are supported choices.

**Target language**: The language the user wants to read. Korean, Japanese,
English, and Thai are supported choices.

**Seamless reading**: The acceptance goal of reading a manga or webtoon in the
target language without noticing original-language text, including during scrolling.
It is a goal to verify, not an unconditional claim about recognized pages.
