// vscode://dollyforge.dollyforge-browser/open?url=http://127.0.0.1:7780/ 를 받으면
// VS Code 의 통합 브라우저(Open Integrated Browser)로 주소를 연다.
// 자체 WebviewPanel 을 만들지 않는다: 통합 브라우저는 최상위 문서로 로드되므로
// pointer lock 이 그대로 동작하고, stream.html 은 게임 서버에서 직접 받아 그린다.
const vscode = require('vscode');

exports.activate = (context) => {
  context.subscriptions.push(vscode.window.registerUriHandler({
    async handleUri(uri) {
      const url = new URLSearchParams(uri.query).get('url');
      if (!url || !/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?\//.test(url)) return;
      try {
        await vscode.commands.executeCommand('workbench.action.browser.open', url);
      } catch {
        // 통합 브라우저 명령이 없으면 Simple Browser 로 폴백한다
        try { await vscode.commands.executeCommand('simpleBrowser.show', url); } catch {}
      }
    },
  }));
};
