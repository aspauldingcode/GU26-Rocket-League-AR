#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <WebKit/WebKit.h>

@interface CardboardXRCastHostView : NSObject
@property(nonatomic, strong) WKWebView *webView;
@end

@implementation CardboardXRCastHostView
@end

static void CardboardXR_CastRunOnMain(void (^block)(void))
{
    if (block == nil)
        return;
    // Unity calls these DllImports from the main thread. dispatch_sync(main) from main deadlocks
    // and the OS watchdog kills the app right after a Share-view QR scan opens the host WebView.
    if ([NSThread isMainThread])
        block();
    else
        dispatch_sync(dispatch_get_main_queue(), block);
}

static UIView *CardboardXR_CastRootView(void)
{
    UIApplication *app = UIApplication.sharedApplication;
    if (@available(iOS 13.0, *))
    {
        for (UIScene *scene in app.connectedScenes)
        {
            if (![scene isKindOfClass:[UIWindowScene class]])
                continue;
            UIWindowScene *windowScene = (UIWindowScene *)scene;
            for (UIWindow *window in windowScene.windows)
            {
                if (window.isKeyWindow && window.rootViewController.view != nil)
                    return window.rootViewController.view;
            }
            if (windowScene.windows.firstObject.rootViewController.view != nil)
                return windowScene.windows.firstObject.rootViewController.view;
        }
    }
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    UIWindow *key = app.keyWindow;
#pragma clang diagnostic pop
    if (key.rootViewController.view != nil)
        return key.rootViewController.view;
    return app.windows.firstObject.rootViewController.view;
}

extern "C" {

void *CardboardXR_CastOpen(const char *url)
{
    if (url == NULL)
        return NULL;

    NSString *urlString = [NSString stringWithUTF8String:url];
    NSURL *nsUrl = [NSURL URLWithString:urlString];
    if (nsUrl == nil)
        return NULL;

    CardboardXRCastHostView *host = [CardboardXRCastHostView new];
    WKWebViewConfiguration *config = [WKWebViewConfiguration new];
    config.allowsInlineMediaPlayback = YES;
    if (@available(iOS 10.0, *))
        config.mediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypeNone;

    CardboardXR_CastRunOnMain(^{
        WKWebView *webView = [[WKWebView alloc] initWithFrame:CGRectMake(-4, -4, 2, 2) configuration:config];
        webView.opaque = NO;
        webView.backgroundColor = UIColor.clearColor;
        webView.hidden = YES;
        UIView *root = CardboardXR_CastRootView();
        if (root != nil)
            [root addSubview:webView];
        [webView loadRequest:[NSURLRequest requestWithURL:nsUrl]];
        host.webView = webView;
    });

    return (void *)CFBridgingRetain(host);
}

void CardboardXR_CastClose(void *handle)
{
    if (handle == NULL)
        return;
    CardboardXRCastHostView *host = CFBridgingRelease(handle);
    CardboardXR_CastRunOnMain(^{
        [host.webView stopLoading];
        [host.webView removeFromSuperview];
        host.webView = nil;
    });
}

void CardboardXR_CastEval(void *handle, const char *js)
{
    if (handle == NULL || js == NULL)
        return;
    CardboardXRCastHostView *host = (__bridge CardboardXRCastHostView *)handle;
    NSString *script = [NSString stringWithUTF8String:js];
    // evaluateJavaScript must not block Unity's player loop.
    dispatch_async(dispatch_get_main_queue(), ^{
        [host.webView evaluateJavaScript:script completionHandler:nil];
    });
}

}
