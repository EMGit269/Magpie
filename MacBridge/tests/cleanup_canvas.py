#! python 3
"""Restore the user's canvas and dispose only the temporary test document."""
import scriptcontext as sc
from Grasshopper import Instances

if 'magpie_mac_test_document' in sc.sticky:
    test_document = sc.sticky['magpie_mac_test_document']
    if Instances.ActiveCanvas.Document == test_document:
        Instances.ActiveCanvas.Document = sc.sticky.get('magpie_mac_previous_document')
    test_document.Dispose()
    del sc.sticky['magpie_mac_test_document']
    sc.sticky.pop('magpie_mac_previous_document', None)
