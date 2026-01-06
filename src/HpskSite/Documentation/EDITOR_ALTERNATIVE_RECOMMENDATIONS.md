# Rich Text Editor Alternatives - Recommendations

**Issue:** TinyMCE requires an API key starting in 2024, with editors transitioning to read-only mode without one.

**Solution:** Replace with a free, open-source alternative that doesn't require API keys.

---

## Top 3 Recommended Alternatives

### 1. **CKEditor 5** ⭐ BEST FOR YOUR USE CASE

**License:** GPL 2+ (Open Source & Free)
**API Key Required:** NO - Use GPL license key
**Self-Hosted:** ✅ Yes
**HTML Support:** ✅ Excellent
**Data Attributes:** ✅ Preserved

**Pros:**
- ✅ Fully open source (GPL 2+) - No API key needed
- ✅ Professional, feature-rich editor
- ✅ Self-hosted without external dependencies
- ✅ Excellent HTML support with data attribute preservation
- ✅ Modular architecture (use only what you need)
- ✅ Great documentation and community support
- ✅ Works with complex HTML structures
- ✅ Collaborative editing capabilities (bonus)
- ✅ Active development and regular updates

**Cons:**
- Slightly larger bundle size (~1.5MB) than alternatives
- Learning curve for advanced customization

**Best For:** Your use case - production-grade HTML editing without API restrictions

**Example Installation:**
```bash
npm install --save @ckeditor/ckeditor5-build-classic
```

**Basic Usage:**
```javascript
import ClassicEditor from '@ckeditor/ckeditor5-build-classic/src/ckeditor';

ClassicEditor
    .create( document.querySelector( '#editor' ) )
    .catch( error => {
        console.error( error );
    } );
```

---

### 2. **Quill** ⭐ LIGHTWEIGHT ALTERNATIVE

**License:** BSD 3-Clause (Open Source & Free)
**API Key Required:** NO
**Self-Hosted:** ✅ Yes
**HTML Support:** ✅ Good (JSON-based)
**Data Attributes:** ⚠️ Partial (via custom formats)

**Pros:**
- ✅ Very lightweight (~43KB minified)
- ✅ Simple, clean API
- ✅ No external dependencies
- ✅ Excellent documentation
- ✅ Great for basic to intermediate editing
- ✅ Fast initialization and performance
- ✅ Modular (only load what you need)

**Cons:**
- ❌ Doesn't preserve HTML as-is (converts to JSON/Delta format)
- ❌ Preserving complex attributes requires custom work
- Less suitable for your complex HTML with data attributes

**Best For:** Simple HTML editing without complex structures or attributes

**Example Installation:**
```bash
npm install quill
```

**Basic Usage:**
```javascript
const quill = new Quill('#editor', {
  theme: 'snow',
  modules: {
    toolbar: [
      ['bold', 'italic', 'underline'],
      ['link', 'image'],
      [{ 'list': 'ordered'}, { 'list': 'bullet' }]
    ]
  }
});
```

---

### 3. **Jodit** ⭐ BALANCED CHOICE

**License:** MIT (Open Source & Free)
**API Key Required:** NO
**Self-Hosted:** ✅ Yes
**HTML Support:** ✅ Excellent
**Data Attributes:** ✅ Preserved

**Pros:**
- ✅ Pure JavaScript (no dependencies)
- ✅ Good HTML support with attribute preservation
- ✅ Feature-rich with many built-in features
- ✅ Plugin system for extensibility
- ✅ Good performance
- ✅ Works well with complex HTML
- ✅ Active development

**Cons:**
- Slightly heavier than Quill
- Smaller community than CKEditor
- Less documentation than CKEditor

**Best For:** Alternative to CKEditor if you prefer something more lightweight

**Example Installation:**
```bash
npm install jodit
```

**Basic Usage:**
```javascript
import { Jodit } from 'jodit';

const editor = Jodit.make('#editor', {
  minHeight: 300,
  height: 300
});
```

---

## Comparison Matrix

| Feature | CKEditor 5 | Quill | Jodit |
|---------|-----------|-------|-------|
| **Open Source** | ✅ GPL 2+ | ✅ BSD 3-Clause | ✅ MIT |
| **API Key Required** | ❌ No | ❌ No | ❌ No |
| **Self-Hosted** | ✅ Yes | ✅ Yes | ✅ Yes |
| **HTML Support** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Data Attributes** | ✅ Preserved | ⚠️ Custom work | ✅ Preserved |
| **Bundle Size** | ~1.5MB | ~43KB | ~300KB |
| **Performance** | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Learning Curve** | Medium | Easy | Easy-Medium |
| **Documentation** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Community** | Very Large | Large | Medium |
| **Active Development** | ✅ Yes | ✅ Yes | ✅ Yes |

---

## RECOMMENDATION FOR HPSK SITE

### **Use CKEditor 5** ✅

**Why:**
1. **Your HTML has complex data attributes** (`data-start`, `data-end`) - CKEditor preserves these perfectly
2. **Production-grade quality** - Handles all edge cases
3. **No API key required** - Fully self-hosted with GPL license
4. **Excellent documentation** - Large community support
5. **Future-proof** - Active development, regular updates
6. **Professional appearance** - Matches your admin interface quality

**Implementation:**
```bash
npm install --save @ckeditor/ckeditor5-build-classic
```

---

## Migration Path from TinyMCE to CKEditor 5

### Step 1: Install CKEditor 5
```bash
npm install --save @ckeditor/ckeditor5-build-classic
```

### Step 2: Replace CDN with NPM Import
**From:**
```html
<script src="https://cdn.tiny.cloud/1/no-api-key/tinymce/6/tinymce.min.js"></script>
```

**To:**
```javascript
import ClassicEditor from '@ckeditor/ckeditor5-build-classic/src/ckeditor';
```

### Step 3: Update SeriesEditModal.cshtml

**Old TinyMCE initialization:**
```javascript
tinymce.init({
    selector: '#seriesDescription',
    height: 300,
    // ... other config
});
```

**New CKEditor 5 initialization:**
```javascript
ClassicEditor
    .create( document.getElementById('seriesDescription'), {
        height: '300px',
        toolbar: [
            'heading', '|',
            'bold', 'italic', 'underline', 'strikethrough', '|',
            'link', 'imageUpload', '|',
            'bulletedList', 'numberedList', '|',
            'blockQuote', 'codeBlock', '|',
            'undo', 'redo'
        ],
        language: 'sv'  // Swedish locale
    })
    .then( editor => {
        // Store reference for content sync
        window.editorInstance = editor;
    } )
    .catch( error => {
        console.error( error );
    } );
```

### Step 4: Update Content Sync

**Old (TinyMCE):**
```javascript
const editor = tinymce.get('seriesDescription');
if (editor) {
    document.getElementById('seriesDescription').value = editor.getContent();
}
```

**New (CKEditor 5):**
```javascript
if (window.editorInstance) {
    document.getElementById('seriesDescription').value = window.editorInstance.getData();
}
```

---

## Technical Considerations

### Data Format
- **TinyMCE:** Returns raw HTML string
- **CKEditor 5:** Also returns raw HTML string (compatible!)

### JSON Parsing (No Changes Needed!)
Your existing JSON parsing in AdminSeriesList.cshtml will work perfectly:
```javascript
// This stays the same - both editors return HTML
if (descriptionContent && descriptionContent.startsWith('{')) {
    try {
        const parsed = JSON.parse(descriptionContent);
        descriptionContent = parsed.markup || descriptionContent;
    } catch (e) {
        // Not JSON, use as-is
    }
}
```

### Data Attribute Preservation
CKEditor 5 automatically preserves custom data attributes:
```html
<!-- This will be preserved exactly as-is -->
<p data-start="339" data-end="342">Text content</p>
<hr data-start="339" data-end="342">
<h2 data-start="344" data-end="365">Heading</h2>
```

---

## Estimated Effort

| Task | Effort |
|------|--------|
| Install CKEditor 5 | 15 minutes |
| Update SeriesEditModal.cshtml | 1 hour |
| Update AdminSeriesList.cshtml | 15 minutes |
| Test and verify | 1 hour |
| **Total** | **2.5 hours** |

---

## Installation & Setup Commands

### Option 1: NPM (Recommended)
```bash
npm install --save @ckeditor/ckeditor5-build-classic
```

### Option 2: CDN (No Build Step)
```html
<script src="https://cdn.ckeditor.com/ckeditor5/41.0.0/classic/ckeditor.js"></script>
```

---

## FAQ

**Q: Will I lose my existing content?**
A: No! CKEditor 5 reads and writes the same HTML format. Your content is safe.

**Q: Do I need an API key?**
A: No! CKEditor 5 is fully self-hosted. You control everything.

**Q: Will it handle my complex HTML?**
A: Yes! CKEditor 5 is excellent with complex HTML and data attributes.

**Q: Is it production-ready?**
A: Yes! CKEditor 5 is used by major organizations worldwide.

**Q: What about Swedish locale?**
A: CKEditor 5 includes Swedish locale support out of the box.

---

## Next Steps

### Immediate Action
1. Create a test branch
2. Install CKEditor 5 via npm
3. Update SeriesEditModal.cshtml with new initialization
4. Test with existing series content
5. Verify data attribute preservation
6. Merge and deploy

### No Rush
You have time to implement this since the current TinyMCE setup works, but doing it now avoids future API key issues.

---

## Additional Resources

- **CKEditor 5 Docs:** https://ckeditor.com/docs/ckeditor5/latest/
- **CKEditor 5 GitHub:** https://github.com/ckeditor/ckeditor5
- **Installation Guide:** https://ckeditor.com/docs/ckeditor5/latest/getting-started/installation/
- **License Info:** https://ckeditor.com/legal/ckeditor-licensing-options/

---

## Conclusion

**CKEditor 5 is the best replacement for TinyMCE** in your use case because:

1. ✅ **No API keys** - Fully self-hosted and free
2. ✅ **Handles your HTML** - Preserves data attributes perfectly
3. ✅ **Production-grade** - Used by major organizations
4. ✅ **Quick migration** - ~2.5 hours of work
5. ✅ **Future-proof** - Active development and support
6. ✅ **Cost savings** - Free forever, no license concerns

The investment of 2-3 hours now saves you from API key management headaches in the future.
