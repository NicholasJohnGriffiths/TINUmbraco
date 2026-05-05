(function () {
    const dashboard = document.querySelector('[data-migration-dashboard]');
    if (!dashboard) {
        return;
    }

    const statusUrl = dashboard.getAttribute('data-status-url');
    const runUrl = dashboard.getAttribute('data-run-url');
    const preflightUrl = dashboard.getAttribute('data-preflight-url');
    const runLiveButton = dashboard.querySelector('[data-run-button-live]');
    const runDryButton = dashboard.querySelector('[data-run-button-dry]');
    const preflightButton = dashboard.querySelector('[data-preflight-button]');
    const jsonPathInput = dashboard.querySelector('[data-json-path-input]');
    let pollHandle = null;

    function setText(fieldName, value, fallback) {
        const element = dashboard.querySelector(`[data-field="${fieldName}"]`);
        if (!element) {
            return;
        }

        element.textContent = value ?? fallback ?? '-';
    }

    function renderPreflight(preflight) {
        if (!preflight) {
            return;
        }

        setText(
            'preflightSummary',
            `File: ${preflight.selectedJsonPath || '-'} | Items: ${preflight.totalItems} | Image URLs: ${preflight.mediaUrlCount} | Media root: ${preflight.mediaRootExists ? 'Found' : 'Missing'}`,
            'No preflight check yet.');

        const folderLines = Array.isArray(preflight.folders)
            ? preflight.folders.map(folder => `${folder.name}: ${folder.exists ? 'Found' : 'Missing'} (Referenced items: ${folder.referencedItemCount})`)
            : [];

        renderLines('preflightFolders', folderLines, 'tool-log-empty', 'No folder details.');
        renderLines('preflightMessages', preflight.messages || [], 'tool-log-empty', 'No preflight messages.');
    }

    function renderLines(fieldName, lines, emptyClass, emptyText) {
        const element = dashboard.querySelector(`[data-field="${fieldName}"]`);
        if (!element) {
            return;
        }

        element.innerHTML = '';

        if (!Array.isArray(lines) || lines.length === 0) {
            const empty = document.createElement('p');
            empty.className = emptyClass;
            empty.textContent = emptyText;
            element.appendChild(empty);
            return;
        }

        for (const line of lines) {
            const item = document.createElement('div');
            item.className = fieldName === 'errors' ? 'tool-error-line' : 'tool-log-line';
            item.textContent = line;
            element.appendChild(item);
        }

        element.scrollTop = element.scrollHeight;
    }

    function formatDate(value) {
        if (!value) {
            return '-';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return value;
        }

        return date.toLocaleString();
    }

    function updateButton(snapshot) {
        const canStart = Boolean(snapshot.canStart) && !snapshot.isRunning;

        if (runLiveButton) {
            runLiveButton.disabled = !canStart;
            runLiveButton.textContent = snapshot.isRunning && !snapshot.isDryRun
                ? 'Live Run Running'
                : 'Run Live';
        }

        if (runDryButton) {
            runDryButton.disabled = !canStart;
            runDryButton.textContent = snapshot.isRunning && snapshot.isDryRun
                ? 'Dry Run Running'
                : 'Run Dry';
        }
    }

    function updateInputs(snapshot) {
        if (!jsonPathInput) {
            return;
        }

        // Preserve the user's manual JSON selection while polling status.
        // Only hydrate from snapshot if the input is currently empty.
        if (!jsonPathInput.value) {
            jsonPathInput.value = snapshot.selectedJsonPath ?? '';
        }
    }

    function updateProgress(snapshot) {
        const current = Number(snapshot.current ?? 0);
        const total = Number(snapshot.total ?? 0);
        const percent = total > 0
            ? Math.min(100, Math.max(0, Math.round((current / total) * 100)))
            : 0;

        setText('progressPercent', `${percent}%`, '0%');

        const progressBar = dashboard.querySelector('[data-field="progressBar"]');
        const progressFill = dashboard.querySelector('[data-field="progressFill"]');

        if (progressFill) {
            progressFill.style.width = `${percent}%`;
        }

        if (progressBar) {
            progressBar.setAttribute('aria-valuenow', String(percent));
        }
    }

    function render(snapshot) {
        setText('configuredJsonPath', snapshot.configuredJsonPath, 'Not configured');
        setText('status', snapshot.status, 'Ready');
        setText('runModeLabel', snapshot.runModeLabel, 'Migration');
        setText('lastMessage', snapshot.lastMessage, '-');
        setText('currentItemName', snapshot.currentItemName, 'Waiting');
        setText('currentWordPressType', snapshot.currentWordPressType, '-');
        setText('currentContentTypeAlias', snapshot.currentContentTypeAlias, '-');
        setText('current', String(snapshot.current ?? 0), '0');
        setText('total', String(snapshot.total ?? 0), '0');
        setText('succeeded', String(snapshot.succeeded ?? 0), '0');
        setText('failed', String(snapshot.failed ?? 0), '0');
        setText('skipped', String(snapshot.skipped ?? 0), '0');
        setText('startedUtc', formatDate(snapshot.startedUtc), '-');
        setText('finishedUtc', formatDate(snapshot.finishedUtc), '-');

        updateProgress(snapshot);
        renderLines('logEntries', snapshot.logEntries, 'tool-log-empty', 'No migration activity yet.');
        renderLines('errors', snapshot.errors, 'tool-log-empty', 'No errors recorded.');
        updateInputs(snapshot);
        updateButton(snapshot);
    }

    async function refresh() {
        if (!statusUrl) {
            return;
        }

        const response = await fetch(statusUrl, { headers: { 'X-Requested-With': 'fetch' } });
        if (!response.ok) {
            return;
        }

        const snapshot = await response.json();
        render(snapshot);
    }

    async function startRun(dryRun) {
        if (!runUrl) {
            return;
        }

        if (runLiveButton) {
            runLiveButton.disabled = true;
        }

        if (runDryButton) {
            runDryButton.disabled = true;
        }

        const requestBody = {
            jsonPath: jsonPathInput ? jsonPathInput.value : null,
            dryRun: Boolean(dryRun)
        };

        const response = await fetch(runUrl, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Requested-With': 'fetch'
            },
            body: JSON.stringify(requestBody)
        });

        if (!response.ok) {
            if (runLiveButton) {
                runLiveButton.disabled = false;
            }

            if (runDryButton) {
                runDryButton.disabled = false;
            }

            return;
        }

        const payload = await response.json();
        if (payload.snapshot) {
            render(payload.snapshot);
        }
    }

    async function runPreflight() {
        if (!preflightUrl) {
            return;
        }

        if (preflightButton) {
            preflightButton.disabled = true;
            preflightButton.textContent = 'Checking...';
        }

        const requestBody = {
            jsonPath: jsonPathInput ? jsonPathInput.value : null,
            dryRun: true
        };

        const response = await fetch(preflightUrl, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Requested-With': 'fetch'
            },
            body: JSON.stringify(requestBody)
        });

        if (response.ok) {
            const payload = await response.json();
            renderPreflight(payload);
        }

        if (preflightButton) {
            preflightButton.disabled = false;
            preflightButton.textContent = 'Check Preflight';
        }
    }

    if (runLiveButton) {
        runLiveButton.addEventListener('click', function () {
            void startRun(false);
        });
    }

    if (runDryButton) {
        runDryButton.addEventListener('click', function () {
            void startRun(true);
        });
    }

    if (preflightButton) {
        preflightButton.addEventListener('click', function () {
            void runPreflight();
        });
    }

    void refresh();
    pollHandle = window.setInterval(function () {
        void refresh();
    }, 1500);

    window.addEventListener('beforeunload', function () {
        if (pollHandle) {
            window.clearInterval(pollHandle);
        }
    });
}());