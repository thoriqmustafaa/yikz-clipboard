export type PanelTab = 'devices' | 'storage' | 'activity' | 'settings' | 'whatsnew';

class Ui {
  panelRequest = $state<PanelTab | null>(null);
  releaseRevision = $state(0);
  announcedVersion = $state<string | null>(null);

  openPanel(tab: PanelTab): void {
    this.panelRequest = tab;
  }

  releaseAvailable(version: string): void {
    this.announcedVersion = version;
    this.releaseRevision++;
  }
}

export const ui = new Ui();
