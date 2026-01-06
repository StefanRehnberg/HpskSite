# Controller Routing Issue - Post-Migration Task

## Problem Statement

During the Precision Competition Type migration, we moved `StartListController` from `HpskSite.Controllers` to `HpskSite.CompetitionTypes.Precision.Controllers` and renamed it to `PrecisionStartListController`.

However, this broke the routing because:
- Umbraco's SurfaceController routing is based on the controller's location relative to the root
- Controllers in nested namespaces don't automatically register with Umbraco's routing system
- The old route `/umbraco/surface/startlist/` stopped working
- All AJAX calls from views were returning 404 errors

## Current (Temporary) Solution

We created a **proxy/wrapper controller** that inherits from the real implementation:

```csharp
// File: C:\Repos\HpskSite\Controllers\StartListController.cs
namespace HpskSite.Controllers
{
    public class StartListController : PrecisionStartListController
    {
        // Inherits all methods from PrecisionStartListController
        // Responds to /umbraco/surface/startlist/ route
    }
}
```

This works but is **NOT ideal** because:
1. Code duplication - the controller exists in two places
2. Controller bloat in root Controllers folder
3. Breaks the clean architecture we're trying to achieve
4. Makes it harder to understand the actual implementation location

## Proper Solution (TODO)

After completing the PRECISION_TYPE_MIGRATION_PLAN, we need to implement one of these approaches:

### Option 1: Custom Route Registration (Recommended)
Create a Composer to explicitly register SurfaceController routes for nested namespaces:

```csharp
// File: C:\Repos\HpskSite\Composers\NestedControllerRouteComposer.cs
public class NestedControllerRouteComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.Configure<UmbracoRequestLoopSettings>(settings =>
        {
            settings.ExcludedPaths.Add("/umbraco/surface/precisionstartlist");
        });
        
        // Register explicit routes for nested controllers
        // This allows PrecisionStartListController to respond to 
        // /umbraco/surface/precisionstartlist/ without a proxy
    }
}
```

### Option 2: Abstract Base Controller Pattern
Keep a base generic StartListController in root, have competition type-specific implementations inherit from it:

```csharp
// Generic base in root
namespace HpskSite.Controllers
{
    public abstract class StartListController : SurfaceController
    {
        // Common start list logic
    }
}

// Precision-specific in nested folder
namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class PrecisionStartListController : StartListController
    {
        // Precision-specific logic
    }
}
```

### Option 3: Update All AJAX URLs
Update all view JavaScript to use the correct nested route immediately:

```javascript
// Change from:
fetch('/umbraco/surface/startlist/GetStartLists?...')

// To:
fetch('/umbraco/surface/precisionstartlist/GetStartLists?...')
```

Then delete the proxy controller entirely. This requires Umbraco to recognize the nested namespace routing.

## Files Affected

- `C:\Repos\HpskSite\Controllers\StartListController.cs` - Proxy controller (temporary)
- `C:\Repos\HpskSite\CompetitionTypes\Precision\Controllers\PrecisionStartListController.cs` - Real implementation
- `C:\Repos\HpskSite\Views\CompetitionManagement.cshtml` - AJAX calls
- `C:\Repos\HpskSite\Views\Partials\CompetitionStartListManagement.cshtml` - AJAX calls

## Testing Requirements

After implementing the proper solution, test:
1. ✓ Start list generation works
2. ✓ Start list retrieval works
3. ✓ Finals qualification calculation works
4. ✓ All AJAX calls resolve correctly
5. ✓ No 404 errors in browser console
6. ✓ Navigation between tabs works smoothly

## Priority

**MEDIUM** - This is technical debt that should be addressed during/after the migration, but the current temporary solution works and doesn't affect functionality.

## View File Organization (Related Issue)

Similar to the controller routing challenge, **view file organization is also constrained by Umbraco platform conventions**.

Unlike controllers which could theoretically be registered in nested namespaces with custom routing, **Umbraco's template resolution for document types cannot be customized easily** without implementing a custom `IViewLocationExpander`.

**Current Decision:** View files follow Umbraco conventions - document type templates in `/Views/` root, partials can be organized in `/Views/Partials/{Type}/` subfolders.

For details, see:
- `CompetitionTypes/README.md` - "View File Organization" section
- `COMPETITION_TYPES_ARCHITECTURE_GUIDE.md` - "View Files and Umbraco Template Conventions" section

This is a separate but related architectural consideration that affects how we organize frontend code in the Competition Types structure.

## Related Documentation

- `PRECISION_TYPE_MIGRATION_PLAN.md` - Main migration plan
- `CompetitionTypes/README.md` - View file organization
- `COMPETITION_TYPES_ARCHITECTURE_GUIDE.md` - Comprehensive architecture guide
- Umbraco v16 SurfaceController routing documentation

---

*Created: October 20, 2025*
*Updated: November 1, 2025 - Added view file organization note*
*Status: PENDING - Waiting for migration completion*
