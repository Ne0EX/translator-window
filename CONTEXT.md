# Local live translator

Translation of text visible on the reader's screen, presented alongside the live
source. Capture choice and subtitle presentation are independent.

## Language

### Reading and capture

**Reader**: The person reading the source content with translated captions.
_Avoid_: Reader when referring to the source application

**Source application**: The application displaying the content being read, such
as a manga viewer or browser.

**Source content**: The original text, artwork, and diagrams displayed by the
source application, independent of translated captions.

**Page**: A content unit identified by the source publication or source
application; one page may extend beyond the visible view.
_Avoid_: Page as a synonym for captured view

**Capture target**: The selected area, window, or screen from which visible
source content is taken.
_Avoid_: Source when the intended meaning is capture target

**Selected area**: A fixed rectangle chosen by the reader on the desktop.

**Window**: A selected application window whose visible position is tracked.

**Screen**: A selected display's visible content.

**Live view**: The source content currently visible within the capture target,
including ongoing scrolling and page-turn animation.
_Avoid_: Captured view when referring to what is visible now

**Captured view**: The visible content within the capture target at a particular
moment; it may include part of a page, several pages, or application controls.
_Avoid_: Page, live view

**Translation session**: A period of live reading with one capture target,
source language, target language, and subtitle presentation, spanning successive
captured views.
_Avoid_: Page translation, one translation request

### Languages and processing

**Source language**: The language selected for recognizing and interpreting the
visible source text.

**Target language**: The language selected for translated captions.

**Local processing**: Recognition and translation performed on the reader's
computer; captured images and recognized text are not sent to translation services.

**Text detection**: Identification of areas in a captured view that appear to
contain source text.
_Avoid_: Recognition when only text locations are known

**Text region**: A bounded area identified as likely containing source text,
such as a speech passage or chart label; its recognized text may still be unavailable.
_Avoid_: Sentence, bounding box when referring to both the area and its text

**Text recognition**: Reading the characters within a text region.
_Avoid_: Translation

**Recognized text**: The source-language text obtained from a text region;
it may differ from what the source actually says.
_Avoid_: Source text when assuming recognition is correct

**Translation**: A target-language rendering of recognized text; its existence
does not establish that it preserves the source meaning.
_Avoid_: Caption when referring only to the language result

**Chart label**: A distinct piece of text associated with an entry in a diagram,
such as a person's name, generation number, or status.
_Avoid_: Whole-chart paragraph

**Chart connector**: A line or branch indicating a relationship between diagram
entries; it is part of the source content.

### Caption presentation

**Caption**: Translated text presented for an associated source text region.
_Avoid_: Translation when referring specifically to its visible presentation

**Caption layer**: The presentation surface containing captions, source covers,
and association badges above the source application.
_Avoid_: Overlay when the distinction from subtitle overlay matters

**Subtitle overlay**: The presentation mode placing translated captions beside
their source text.
_Avoid_: Overwrite, caption layer

**Subtitle overwrite**: The presentation mode placing translated captions over
opaque covers on source text; the source content itself remains unchanged.
_Avoid_: Source editing, inpainting

**Source cover**: An opaque area of the caption layer that conceals source text
for subtitle overwrite.
_Avoid_: Erased text, modified artwork

**Caption placement**: The position, size, and line arrangement of a caption in
relation to its source text and the available reading space.

**Margin caption**: A caption placed in a margin when there is insufficient
readable space near its source text.

**Association badge**: A numbered marker linking a margin caption to its source
text region.

### Navigation and caption continuity

**Navigation**: A reader action changing the live view, such as scrolling,
turning a page, or changing zoom.

**Progressive captions**: Captions presented as individual results become
available while other text regions are still being processed.
_Avoid_: Complete page translation

**Caption suspension**: Temporary hiding of captions while their relevance to
the moving or changed live view is uncertain; the translation session remains active.
_Avoid_: Stop, discarded captions

**Caption reuse**: Presentation of an existing caption for matching source text
in a later live view, with placement appropriate to that view.

**Session stop**: The end of a translation session, including removal of its
captions and rejection of any later results from that session.
_Avoid_: Caption suspension

### Reading quality and feedback

**Detection miss**: Visible source text for which no text region is identified.
_Avoid_: Unreadable region, translation failure

**Region grouping error**: Separate source passages combined into one text
region, or one passage divided into unrelated regions, such as several chart
labels treated as one paragraph.
_Avoid_: Translation error when the grouping is already wrong before translation

**Unreadable region**: A detected text region whose recognition has finished
without recognized text; this describes a system outcome, not human readability.
_Avoid_: Undetected text, translation error

**Recognition error**: Recognized text that differs from the characters in its
source region.
_Avoid_: Translation error when the mistake is already in the recognized text

**Translation error**: A translation that changes or loses the meaning of the
recognized text, including names, relationships, numbers, or status notes.
_Avoid_: Translation failure when a result exists but says the wrong thing

**Translation failure**: An attempted translation ending without a complete
translation result for the recognized text.
_Avoid_: Unreadable region, missing caption when placement may be the cause

**Caption placement failure**: The inability to present an available translation
within the available space and the readability and placement constraints.
_Avoid_: Translation failure

**Caption misalignment**: A caption or source cover positioned against the
wrong source region or offset from its intended region.
_Avoid_: Translation error when the text itself is correct

**Stale caption**: A caption presented for source content that no longer matches
the live view.
_Avoid_: Reused caption when its source content still matches

**First-caption delay**: The time from session start or the end of navigation
until the first matching caption becomes visible, with the starting event stated.
_Avoid_: Translation time when capture, recognition, or placement is included

**Source stutter**: Visible interruptions in the source application's scrolling
or page-turn motion, regardless of whether captions have finished appearing.
_Avoid_: Caption delay when the artwork itself is stuttering

**Seamless reading**: The acceptance goal of reading manga or webtoons in the
target language without noticing original-language text or interruptions from
caption presentation, including during navigation; it is not a current guarantee.
