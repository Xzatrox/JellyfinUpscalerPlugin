# Workspace Refactoring Plan

## Overview
This document outlines the refactoring strategy to ensure no source file exceeds 400 lines, organizing code into logical entities.

## Files Requiring Refactoring

### 1. Controllers/UpscalerController.cs (2203 lines) ✅ IN PROGRESS
**Strategy**: Split into multiple endpoint controllers and helpers

**New Structure**:
- `Controllers/UpscalerController.cs` (< 400 lines) - Main controller, coordination
- `Controllers/Endpoints/ModelEndpoints.cs` (< 300 lines) - Model management ✅ CREATED
- `Controllers/Endpoints/ImageEndpoints.cs` (< 350 lines) - Image upscaling ✅ CREATED
- `Controllers/Endpoints/VideoEndpoints.cs` (< 350 lines) - Video processing
- `Controllers/Endpoints/QueueEndpoints.cs` (< 250 lines) - Queue management
- `Controllers/Endpoints/SettingsEndpoints.cs` (< 350 lines) - Settings import/export
- `Controllers/Endpoints/DiagnosticsEndpoints.cs` (< 350 lines) - Health, benchmarks
- `Controllers/Endpoints/ProxyEndpoints.cs` (< 300 lines) - AI service proxies
- `Controllers/Helpers/RateLimiter.cs` (< 100 lines) - Rate limiting ✅ CREATED
- `Controllers/Helpers/ValidationHelper.cs` (< 150 lines) - Input validation ✅ CREATED

### 2. docker-ai-service/app/main.py (4325 lines)
**Strategy**: Split into modular Python components

**New Structure**:
- `docker-ai-service/app/main.py` (< 200 lines) - FastAPI app initialization
- `docker-ai-service/app/routes/upscale_routes.py` (< 350 lines) - Upscaling endpoints
- `docker-ai-service/app/routes/model_routes.py` (< 300 lines) - Model management
- `docker-ai-service/app/routes/health_routes.py` (< 200 lines) - Health/diagnostics
- `docker-ai-service/app/routes/face_restore_routes.py` (< 250 lines) - Face restoration
- `docker-ai-service/app/services/model_manager.py` (< 400 lines) - Model loading/caching
- `docker-ai-service/app/services/inference_service.py` (< 400 lines) - Inference logic
- `docker-ai-service/app/services/gpu_manager.py` (< 300 lines) - GPU detection/management
- `docker-ai-service/app/middleware/auth.py` (< 150 lines) - Authentication
- `docker-ai-service/app/middleware/rate_limit.py` (< 150 lines) - Rate limiting
- `docker-ai-service/app/utils/image_processing.py` (< 300 lines) - Image utilities
- `docker-ai-service/app/config.py` (< 200 lines) - Configuration

### 3. Configuration/configurationpage.html (2450 lines)
**Strategy**: Split into modular HTML components with includes

**New Structure**:
- `Configuration/configurationpage.html` (< 300 lines) - Main page structure
- `Configuration/components/basic-settings.html` (< 350 lines) - Basic settings tab
- `Configuration/components/advanced-settings.html` (< 350 lines) - Advanced settings
- `Configuration/components/model-management.html` (< 350 lines) - Model management UI
- `Configuration/components/queue-management.html` (< 300 lines) - Queue UI
- `Configuration/components/diagnostics.html` (< 300 lines) - Diagnostics/health
- `Configuration/components/filter-settings.html` (< 250 lines) - Video filters
- `Configuration/styles/config-styles.css` (< 400 lines) - Extracted CSS

### 4. Configuration/player-integration.js (1220 lines)
**Strategy**: Split into ES6 modules

**New Structure**:
- `Configuration/player-integration.js` (< 200 lines) - Main initialization
- `Configuration/modules/player-ui.js` (< 350 lines) - UI components
- `Configuration/modules/player-events.js` (< 300 lines) - Event handlers
- `Configuration/modules/upscale-controls.js` (< 350 lines) - Upscaling controls
- `Configuration/modules/player-api.js` (< 250 lines) - API communication

### 5. docker-ai-service/static/index.html (859 lines)
**Strategy**: Split into components and extract CSS/JS

**New Structure**:
- `docker-ai-service/static/index.html` (< 250 lines) - Main structure
- `docker-ai-service/static/components/model-selector.html` (< 200 lines)
- `docker-ai-service/static/components/upload-form.html` (< 150 lines)
- `docker-ai-service/static/components/results-display.html` (< 150 lines)
- `docker-ai-service/static/css/styles.css` (< 300 lines) - Extracted CSS
- `docker-ai-service/static/js/app.js` (< 350 lines) - Main JS logic

### 6. Services/CacheManager.cs (598 lines)
**Strategy**: Split into cache operations and statistics

**New Structure**:
- `Services/CacheManager.cs` (< 350 lines) - Core cache operations
- `Services/Cache/CacheStatistics.cs` (< 200 lines) - Statistics tracking
- `Services/Cache/CacheCleanup.cs` (< 200 lines) - Cleanup operations

### 7. Services/ProcessingMethodExecutor.cs (725 lines)
**Strategy**: Split by processing method

**New Structure**:
- `Services/ProcessingMethodExecutor.cs` (< 250 lines) - Coordinator
- `Services/Processing/DirectProcessing.cs` (< 300 lines) - Direct method
- `Services/Processing/RemoteProcessing.cs` (< 300 lines) - Remote SSH method
- `Services/Processing/HybridProcessing.cs` (< 250 lines) - Hybrid method

### 8. Configuration/sidebar-upscaler.js (462 lines)
**Strategy**: Split into modules

**New Structure**:
- `Configuration/sidebar-upscaler.js` (< 200 lines) - Main initialization
- `Configuration/modules/sidebar-ui.js` (< 300 lines) - UI components

### 9. Services/UpscalerCore.cs (435 lines)
**Strategy**: Extract hardware detection and model resolution

**New Structure**:
- `Services/UpscalerCore.cs` (< 300 lines) - Core upscaling logic
- `Services/Upscaler/HardwareDetection.cs` (< 200 lines) - Hardware detection
- `Services/Upscaler/ModelResolver.cs` (< 200 lines) - Model selection logic

### 10. Services/VideoFrameProcessor.cs (417 lines)
**Strategy**: Split frame extraction and processing

**New Structure**:
- `Services/VideoFrameProcessor.cs` (< 250 lines) - Main processor
- `Services/Video/FrameExtractor.cs` (< 250 lines) - Frame extraction logic

### 11. Configuration/quick-menu.js (415 lines)
**Strategy**: Split into modules

**New Structure**:
- `Configuration/quick-menu.js` (< 200 lines) - Main initialization
- `Configuration/modules/quick-menu-ui.js` (< 250 lines) - UI components

### 12. PluginConfiguration.cs (401 lines)
**Strategy**: Split into logical configuration groups

**New Structure**:
- `PluginConfiguration.cs` (< 200 lines) - Main config class
- `Configuration/CoreSettings.cs` (< 200 lines) - Core settings
- `Configuration/AdvancedSettings.cs` (< 200 lines) - Advanced settings

## Implementation Status

### Completed ✅
1. Controllers/Helpers/RateLimiter.cs - Rate limiting helper
2. Controllers/Helpers/ValidationHelper.cs - Input validation helper
3. Controllers/Endpoints/ModelEndpoints.cs - Model management endpoints
4. Controllers/Endpoints/ImageEndpoints.cs - Image upscaling endpoints

### In Progress 🔄
- Controllers/UpscalerController.cs refactoring

### Pending ⏳
- All other files listed above

## Benefits of This Refactoring

1. **Maintainability**: Smaller files are easier to understand and modify
2. **Testability**: Isolated components are easier to unit test
3. **Reusability**: Extracted helpers can be reused across the codebase
4. **Collaboration**: Multiple developers can work on different files without conflicts
5. **Performance**: Better code organization can lead to improved build times
6. **Readability**: Clear separation of concerns makes code easier to navigate

## Next Steps

1. Complete UpscalerController.cs refactoring (create remaining endpoint files)
2. Refactor docker-ai-service/app/main.py (Python backend)
3. Refactor Configuration HTML/JS files (frontend)
4. Refactor remaining Services files
5. Update imports and references across the codebase
6. Run tests to ensure functionality is preserved
7. Update documentation to reflect new structure

## Notes

- All refactored files maintain backward compatibility
- Existing functionality is preserved
- New structure follows SOLID principles
- Each file has a single, well-defined responsibility
