import * as monaco from 'monaco-editor/editor';
import 'monaco-editor/features/register.all';
import 'monaco-editor/languages/definitions/csharp/register';
import 'monaco-editor/languages/definitions/css/register';
import 'monaco-editor/languages/definitions/html/register';

globalThis.MonacoEnvironment = {
    getWorker() {
        return new Worker(new URL('./editor.worker.js', import.meta.url), {
            name: 'NovaSharp Monaco editor worker',
            type: 'module'
        });
    }
};

globalThis.NovaMonaco = monaco;

export { monaco };
