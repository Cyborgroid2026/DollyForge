// vscode://dollyforge.dollyforge-browser/open?url=http://127.0.0.1:7780/ 를 받으면 통합 브라우저로 연다.
// 이 컴퓨터 안의 주소(localhost, 127.0.0.1)만 연다.
const vscode = require('vscode');

exports.activate = (context) => {
  context.subscriptions.push(vscode.window.registerUriHandler({
    async handleUri(uri) {
      const url = new URLSearchParams(uri.query).get('url');
      if (!url || !/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?\//.test(url)) return;
      try {
        await vscode.commands.executeCommand('workbench.action.browser.open', url);
      } catch {
        await vscode.commands.executeCommand('simpleBrowser.show', url);
      }
    },
  }));
};
