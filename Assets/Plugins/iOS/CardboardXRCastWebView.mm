#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <WebKit/WebKit.h>

@interface CardboardXRCastHostView : NSObject
@property(nonatomic, strong) WKWebView *webView;
@end

@implementation CardboardXRCastHostView
@end

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

    dispatch_sync(dispatch_get_main_queue(), ^{
        WKWebView *webView = [[WKWebView alloc] initWithFrame:CGRectMake(-4, -4, 2, 2) configuration:config];
        webView.opaque = NO;
        webView.backgroundColor = UIColor.clearColor;
        webView.hidden = YES;
        UIView *root = UIApplication.sharedApplication.keyWindow.rootViewController.view;
        if (root == nil)
            root = UIApplication.sharedApplication.windows.firstObject.rootViewController.view;
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
    dispatch_async(dispatch_get_main_queue(), ^{
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
    dispatch_async(dispatch_get_main_queue(), ^{
        [host.webView evaluateJavaScript:script completionHandler:nil];
    });
}

}
