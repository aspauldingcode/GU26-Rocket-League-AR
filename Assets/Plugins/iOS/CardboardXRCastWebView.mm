#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <WebKit/WebKit.h>

// The host page's status lines (webrtc.js log()) land in the device log as [Cast/Web].
@interface CardboardXRCastLog : NSObject <WKScriptMessageHandler>
@end

@implementation CardboardXRCastLog
- (void)userContentController:(WKUserContentController *)controller didReceiveScriptMessage:(WKScriptMessage *)message
{
    NSLog(@"[Cast/Web] %@", message.body);
}
@end

// Events the host page sends the app (webrtc.js notifyApp), such as "ended:viewer-left",
// queued for Unity to take with CardboardXR_CastTakeEvent. Both run on the main thread.
@interface CardboardXRCastEvents : NSObject <WKScriptMessageHandler>
@property(nonatomic, strong) NSMutableArray<NSString *> *queue;
@end

@implementation CardboardXRCastEvents
- (instancetype)init
{
    if ((self = [super init]))
        _queue = [NSMutableArray new];
    return self;
}

- (void)userContentController:(WKUserContentController *)controller didReceiveScriptMessage:(WKScriptMessage *)message
{
    [self.queue addObject:[NSString stringWithFormat:@"%@", message.body]];
}
@end

@interface CardboardXRCastHostView : NSObject
@property(nonatomic, strong) WKWebView *webView;
@property(nonatomic, strong) CardboardXRCastEvents *events;
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
    [config.userContentController addScriptMessageHandler:[CardboardXRCastLog new] name:@"castlog"];
    host.events = [CardboardXRCastEvents new];
    [config.userContentController addScriptMessageHandler:host.events name:@"castevent"];

    CardboardXR_CastRunOnMain(^{
        // On screen but invisible: WebKit suspends the page of a hidden or off-screen web view,
        // which stopped the stream to the website. One point, nearly transparent, no touches.
        WKWebView *webView = [[WKWebView alloc] initWithFrame:CGRectMake(0, 0, 1, 1) configuration:config];
        webView.opaque = NO;
        webView.backgroundColor = UIColor.clearColor;
        webView.alpha = 0.01;
        webView.userInteractionEnabled = NO;
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
        WKWebView *webView = host.webView;
        host.webView = nil;
        if (webView == nil)
            return;
        // Let the page drop its PeerJS link first, so the website sees the close at once.
        [webView evaluateJavaScript:@"try{window.__cast&&window.__cast.destroy()}catch(e){}"
                  completionHandler:^(id result, NSError *error) {
            [webView stopLoading];
            [webView.configuration.userContentController removeScriptMessageHandlerForName:@"castlog"];
            [webView.configuration.userContentController removeScriptMessageHandlerForName:@"castevent"];
            [webView removeFromSuperview];
        }];
    });
}

// The oldest event not yet taken, or NULL. The marshaller frees the copy.
const char *CardboardXR_CastTakeEvent(void *handle)
{
    if (handle == NULL)
        return NULL;
    CardboardXRCastHostView *host = (__bridge CardboardXRCastHostView *)handle;
    NSMutableArray<NSString *> *queue = host.events.queue;
    if (queue.count == 0)
        return NULL;
    NSString *event = queue.firstObject;
    [queue removeObjectAtIndex:0];
    return strdup(event.UTF8String);
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
