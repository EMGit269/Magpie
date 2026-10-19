Locally bundled browser dependencies (no CDN requests at runtime):

- marked 15.0.12: https://github.com/markedjs/marked — MIT, see marked.LICENSE.md.
- DOMPurify 3.3.3: https://github.com/cure53/DOMPurify — Apache-2.0 OR MPL-2.0, see purify.LICENSE.

The chat renders Markdown with marked and sanitizes the resulting HTML with DOMPurify and a restricted tag/attribute list. Model output cannot add scripts, images, event handlers, or unsafe links.
