import * as XLSX from 'xlsx-js-style';

const XLSX_MIME = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

/**
 * Guarda un libro de Excel mostrando el diálogo "Guardar como" del sistema
 * (File System Access API) para que el usuario elija nombre y carpeta.
 *
 * En navegadores que no soportan showSaveFilePicker (Firefox, Safari) o si el
 * usuario no cancela, cae a la descarga clásica con el nombre sugerido.
 *
 * @param wb            Libro generado con XLSX.utils.book_new().
 * @param suggestedName Nombre propuesto en el diálogo (con o sin .xlsx).
 */
export async function saveWorkbook(wb: XLSX.WorkBook, suggestedName: string): Promise<void> {
  const name = suggestedName.endsWith('.xlsx') ? suggestedName : `${suggestedName}.xlsx`;
  const data = XLSX.write(wb, { bookType: 'xlsx', type: 'array' });
  const blob = new Blob([data], { type: XLSX_MIME });

  const picker = (window as any).showSaveFilePicker;
  if (typeof picker === 'function') {
    try {
      const handle = await picker.call(window, {
        suggestedName: name,
        types: [{ description: 'Libro de Excel', accept: { [XLSX_MIME]: ['.xlsx'] } }]
      });
      const writable = await handle.createWritable();
      await writable.write(blob);
      await writable.close();
      return;
    } catch (err: any) {
      // El usuario cerró el diálogo: no descargamos nada.
      if (err?.name === 'AbortError') return;
      // Cualquier otro error: caemos a la descarga clásica.
    }
  }

  // Fallback: descarga directa con el nombre sugerido.
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}
